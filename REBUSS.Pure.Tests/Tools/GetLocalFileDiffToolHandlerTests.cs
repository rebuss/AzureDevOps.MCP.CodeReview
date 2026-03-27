using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Services.LocalReview;
using REBUSS.Pure.Services.ResponsePacking;
using REBUSS.Pure.Tools;

namespace REBUSS.Pure.Tests.Tools;

public class GetLocalFileDiffToolHandlerTests
{
    private readonly ILocalReviewProvider _reviewProvider = Substitute.For<ILocalReviewProvider>();
    private readonly IContextBudgetResolver _budgetResolver = Substitute.For<IContextBudgetResolver>();
    private readonly ITokenEstimator _tokenEstimator = Substitute.For<ITokenEstimator>();
    private readonly IFileClassifier _fileClassifier = Substitute.For<IFileClassifier>();
    private readonly GetLocalFileDiffToolHandler _handler;

    private static readonly PullRequestDiff SampleDiff = new()
    {
        Title = "Local changes (working-tree)",
        Status = "local",
        Files = new List<FileChange>
        {
            new()
            {
                Path = "src/Service.cs",
                ChangeType = "edit",
                Additions = 1,
                Deletions = 1,
                Hunks = new List<DiffHunk>
                {
                    new()
                    {
                        OldStart = 5, OldCount = 1, NewStart = 5, NewCount = 1,
                        Lines = new List<DiffLine>
                        {
                            new() { Op = '-', Text = "old code" },
                            new() { Op = '+', Text = "new code" }
                        }
                    }
                }
            }
        }
    };

    public GetLocalFileDiffToolHandlerTests()
    {
        _budgetResolver.Resolve(Arg.Any<int?>(), Arg.Any<string?>())
            .Returns(new BudgetResolutionResult(200000, 140000, BudgetSource.Default, Array.Empty<string>()));
        _tokenEstimator.Estimate(Arg.Any<string>(), Arg.Any<int>())
            .Returns(new TokenEstimationResult(100, 0.07, true));
        _fileClassifier.Classify(Arg.Any<string>())
            .Returns(new FileClassification { Category = FileCategory.Source, Extension = ".cs", IsBinary = false, IsGenerated = false, IsTestFile = false, ReviewPriority = "high" });

        _handler = new GetLocalFileDiffToolHandler(
            _reviewProvider,
            new ResponsePacker(NullLogger<ResponsePacker>.Instance),
            _budgetResolver,
            _tokenEstimator,
            _fileClassifier,
            NullLogger<GetLocalFileDiffToolHandler>.Instance);
    }

    // --- Happy path ---

    [Fact]
    public async Task ExecuteAsync_ReturnsStructuredJson()
    {
        _reviewProvider.GetFileDiffAsync(
                "src/Service.cs", Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(SampleDiff);

        var result = await _handler.ExecuteAsync(CreateArgs("src/Service.cs"));

        Assert.False(result.IsError);
        var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.True(doc.RootElement.TryGetProperty("files", out var files));
        Assert.Equal(1, files.GetArrayLength());

        var file = files[0];
        Assert.Equal("src/Service.cs", file.GetProperty("path").GetString());
        Assert.Equal("edit", file.GetProperty("changeType").GetString());
        Assert.Equal(1, file.GetProperty("additions").GetInt32());

        // prNumber should be absent from local diff output
        Assert.False(doc.RootElement.TryGetProperty("prNumber", out _));

        var hunks = file.GetProperty("hunks");
        Assert.Equal(1, hunks.GetArrayLength());
        var lines = hunks[0].GetProperty("lines");
        Assert.Equal(2, lines.GetArrayLength());
    }

    [Fact]
    public async Task ExecuteAsync_DefaultsToWorkingTree_WhenNoScope()
    {
        _reviewProvider.GetFileDiffAsync(
                Arg.Any<string>(), Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(SampleDiff);

        await _handler.ExecuteAsync(CreateArgs("src/Service.cs"));

        await _reviewProvider.Received(1).GetFileDiffAsync(
            "src/Service.cs",
            Arg.Is<LocalReviewScope>(s => s.Kind == LocalReviewScopeKind.WorkingTree),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ParsesStagedScope()
    {
        _reviewProvider.GetFileDiffAsync(
                Arg.Any<string>(), Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(SampleDiff);

        var args = CreateArgs("src/Service.cs", "staged");
        await _handler.ExecuteAsync(args);

        await _reviewProvider.Received(1).GetFileDiffAsync(
            "src/Service.cs",
            Arg.Is<LocalReviewScope>(s => s.Kind == LocalReviewScopeKind.Staged),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_HandlesScopeAndPathAsJsonElements()
    {
        _reviewProvider.GetFileDiffAsync(
                Arg.Any<string>(), Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(SampleDiff);

        var json = JsonSerializer.Deserialize<Dictionary<string, object>>(
            """{"path":"src/Service.cs","scope":"staged"}""")!;
        var result = await _handler.ExecuteAsync(json);

        Assert.False(result.IsError);
    }

    // --- Validation errors ---

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenArgumentsNull()
    {
        var result = await _handler.ExecuteAsync(null);
        Assert.True(result.IsError);
        Assert.Contains("Missing required parameter", result.Content[0].Text);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenPathMissing()
    {
        var result = await _handler.ExecuteAsync(new Dictionary<string, object>());
        Assert.True(result.IsError);
        Assert.Contains("Missing required parameter", result.Content[0].Text);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenPathEmpty()
    {
        var result = await _handler.ExecuteAsync(new Dictionary<string, object> { ["path"] = "" });
        Assert.True(result.IsError);
        Assert.Contains("must not be empty", result.Content[0].Text);
    }

    // --- Provider exceptions ---

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenRepositoryNotFound()
    {
        _reviewProvider.GetFileDiffAsync(
                Arg.Any<string>(), Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new LocalRepositoryNotFoundException("No repo"));

        var result = await _handler.ExecuteAsync(CreateArgs("src/Service.cs"));

        Assert.True(result.IsError);
        Assert.Contains("Repository not found", result.Content[0].Text);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenFileNotFoundInLocalChanges()
    {
        _reviewProvider.GetFileDiffAsync(
                Arg.Any<string>(), Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new LocalFileNotFoundException("File not found"));

        var result = await _handler.ExecuteAsync(CreateArgs("src/NotChanged.cs"));

        Assert.True(result.IsError);
        Assert.Contains("File not found in local changes", result.Content[0].Text);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_OnUnexpectedException()
    {
        _reviewProvider.GetFileDiffAsync(
                Arg.Any<string>(), Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await _handler.ExecuteAsync(CreateArgs("src/Service.cs"));

        Assert.True(result.IsError);
        Assert.Contains("boom", result.Content[0].Text);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenGitCommandFails()
    {
        _reviewProvider.GetFileDiffAsync(
                Arg.Any<string>(), Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new GitCommandException(128, "fatal: bad revision"));

        var result = await _handler.ExecuteAsync(CreateArgs("src/Service.cs"));

        Assert.True(result.IsError);
        Assert.Contains("Git command failed", result.Content[0].Text);
    }

    // --- Tool definition ---

    [Fact]
    public void GetToolDefinition_HasCorrectName()
    {
        Assert.Equal("get_local_file_diff", _handler.ToolName);
    }

    [Fact]
    public void GetToolDefinition_RequiresPath()
    {
        var def = _handler.GetToolDefinition();
        Assert.Contains("path", def.InputSchema.Required!);
    }

    [Fact]
    public void GetToolDefinition_ScopeIsOptional()
    {
        var def = _handler.GetToolDefinition();
        Assert.DoesNotContain("scope", def.InputSchema.Required!);
        Assert.True(def.InputSchema.Properties.ContainsKey("scope"));
    }

    [Fact]
    public void GetToolDefinition_HasPackingParameters()
    {
        var def = _handler.GetToolDefinition();
        Assert.True(def.InputSchema.Properties.ContainsKey("modelName"));
        Assert.True(def.InputSchema.Properties.ContainsKey("maxTokens"));
    }

    // --- Packing integration ---

    [Fact]
    public async Task ExecuteAsync_IncludesManifest_InResponse()
    {
        _reviewProvider.GetFileDiffAsync(
                "src/Service.cs", Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(SampleDiff);

        var result = await _handler.ExecuteAsync(CreateArgs("src/Service.cs"));

        Assert.False(result.IsError);
        var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.True(doc.RootElement.TryGetProperty("manifest", out var manifest));
        Assert.True(manifest.TryGetProperty("summary", out var summary));
        Assert.Equal(1, summary.GetProperty("totalItems").GetInt32());
    }

    // --- Helpers ---

    private static Dictionary<string, object> CreateArgs(string path, string? scope = null)
    {
        var args = new Dictionary<string, object> { ["path"] = path };
        if (scope is not null)
            args["scope"] = scope;
        return args;
    }
}
