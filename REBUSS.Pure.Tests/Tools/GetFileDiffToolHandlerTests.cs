using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Services.ResponsePacking;
using REBUSS.Pure.Tools;

namespace REBUSS.Pure.Tests.Tools;

public class GetFileDiffToolHandlerTests
{
    private readonly IPullRequestDataProvider _diffProvider = Substitute.For<IPullRequestDataProvider>();
    private readonly IContextBudgetResolver _budgetResolver = Substitute.For<IContextBudgetResolver>();
    private readonly ITokenEstimator _tokenEstimator = Substitute.For<ITokenEstimator>();
    private readonly IFileClassifier _fileClassifier = Substitute.For<IFileClassifier>();
    private readonly GetFileDiffToolHandler _handler;

    private static readonly PullRequestDiff SampleFileDiff = new()
    {
        Title = "Fix bug",
        Status = "active",
        SourceBranch = "feature/x",
        TargetBranch = "main",
        SourceRefName = "refs/heads/feature/x",
        TargetRefName = "refs/heads/main",
        Files = new List<FileChange>
        {
            new()
            {
                Path = "/src/A.cs",
                ChangeType = "edit",
                Additions = 1,
                Deletions = 1,
                Hunks = new List<DiffHunk>
                {
                    new()
                    {
                        OldStart = 1, OldCount = 1, NewStart = 1, NewCount = 1,
                        Lines = new List<DiffLine>
                        {
                            new() { Op = '-', Text = "old" },
                            new() { Op = '+', Text = "new" }
                        }
                    }
                }
            }
        }
    };

    public GetFileDiffToolHandlerTests()
    {
        _budgetResolver.Resolve(Arg.Any<int?>(), Arg.Any<string?>())
            .Returns(new BudgetResolutionResult(200000, 140000, BudgetSource.Default, Array.Empty<string>()));
        _tokenEstimator.Estimate(Arg.Any<string>(), Arg.Any<int>())
            .Returns(new TokenEstimationResult(100, 0.07, true));
        _fileClassifier.Classify(Arg.Any<string>())
            .Returns(new FileClassification { Category = FileCategory.Source, Extension = ".cs", IsBinary = false, IsGenerated = false, IsTestFile = false, ReviewPriority = "high" });

        _handler = new GetFileDiffToolHandler(
            _diffProvider,
            new ResponsePacker(NullLogger<ResponsePacker>.Instance),
            _budgetResolver,
            _tokenEstimator,
            _fileClassifier,
            NullLogger<GetFileDiffToolHandler>.Instance);
    }

    private static string AllText(IEnumerable<ContentBlock> blocks) =>
        string.Join("\n", blocks.Cast<TextContentBlock>().Select(b => b.Text));

    // --- Happy path ---

    [Fact]
    public async Task ExecuteAsync_ReturnsStructuredJson_ByDefault()
    {
        _diffProvider.GetFileDiffAsync(42, "/src/A.cs", Arg.Any<CancellationToken>())
            .Returns(SampleFileDiff);

        var blocks = (await _handler.ExecuteAsync(42, "/src/A.cs")).ToList();
        var text = AllText(blocks);

        Assert.NotEmpty(blocks);
        Assert.Contains("/src/A.cs", text);
        Assert.Contains("@@ -1,1 +1,1 @@", text);
        Assert.Contains("-old", text);
        Assert.Contains("+new", text);
    }

    [Fact]
    public async Task ExecuteAsync_IncludesManifest_InResponse()
    {
        _diffProvider.GetFileDiffAsync(42, "/src/A.cs", Arg.Any<CancellationToken>())
            .Returns(SampleFileDiff);

        var blocks = (await _handler.ExecuteAsync(42, "/src/A.cs")).ToList();
        var lastBlock = blocks.Cast<TextContentBlock>().Last().Text;

        Assert.Contains("Manifest:", lastBlock);
        Assert.Contains("/src/A.cs", lastBlock);
    }

    // --- Validation errors ---

    [Fact]
    public async Task ExecuteAsync_ThrowsMcpException_WhenPrNumberZero()
    {
        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(0, "/src/A.cs"));

        Assert.Contains("greater than 0", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsMcpException_WhenPrNumberNegative()
    {
        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(-5, "/src/A.cs"));

        Assert.Contains("greater than 0", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsMcpException_WhenPathEmpty()
    {
        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(42, ""));

        Assert.Contains("Missing required parameter: path", ex.Message);
    }

    // --- Provider exceptions ---

    [Fact]
    public async Task ExecuteAsync_ThrowsMcpException_WhenPullRequestNotFound()
    {
        _diffProvider.GetFileDiffAsync(999, "/src/A.cs", Arg.Any<CancellationToken>())
            .ThrowsAsync(new PullRequestNotFoundException("PR #999 not found"));

        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(999, "/src/A.cs"));

        Assert.Contains("Pull Request not found", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsMcpException_WhenFileNotFoundInPullRequest()
    {
        _diffProvider.GetFileDiffAsync(42, "/src/NonExistent.cs", Arg.Any<CancellationToken>())
            .ThrowsAsync(new FileNotFoundInPullRequestException(
                "File '/src/NonExistent.cs' not found in Pull Request #42"));

        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(42, "/src/NonExistent.cs"));

        Assert.Contains("File not found in Pull Request", ex.Message);
        Assert.Contains("NonExistent.cs", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsMcpException_OnUnexpectedException()
    {
        _diffProvider.GetFileDiffAsync(42, "/src/A.cs", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Something broke"));

        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(42, "/src/A.cs"));

        Assert.Contains("Something broke", ex.Message);
    }
}