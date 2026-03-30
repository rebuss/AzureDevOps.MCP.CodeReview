using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Services.ResponsePacking;
using REBUSS.Pure.Tools;

namespace REBUSS.Pure.Tests.Tools;

public class GetPullRequestFilesToolHandlerTests
{
    private readonly IPullRequestDataProvider _filesProvider = Substitute.For<IPullRequestDataProvider>();
    private readonly IContextBudgetResolver _budgetResolver = Substitute.For<IContextBudgetResolver>();
    private readonly ITokenEstimator _tokenEstimator = Substitute.For<ITokenEstimator>();
    private readonly IFileClassifier _fileClassifier = Substitute.For<IFileClassifier>();
    private readonly IPageAllocator _pageAllocator = Substitute.For<IPageAllocator>();
    private readonly IPageReferenceCodec _pageReferenceCodec = Substitute.For<IPageReferenceCodec>();
    private readonly GetPullRequestFilesToolHandler _handler;

    private static readonly PullRequestFiles SampleFiles = new()
    {
        Files = new List<PullRequestFileInfo>
        {
            new()
            {
                Path = "src/Service.cs", Status = "modified",
                Additions = 10, Deletions = 3, Changes = 13,
                Extension = ".cs", IsBinary = false, IsGenerated = false,
                IsTestFile = false, ReviewPriority = "high"
            },
            new()
            {
                Path = "tests/ServiceTests.cs", Status = "modified",
                Additions = 5, Deletions = 1, Changes = 6,
                Extension = ".cs", IsBinary = false, IsGenerated = false,
                IsTestFile = true, ReviewPriority = "medium"
            }
        },
        Summary = new PullRequestFilesSummary
        {
            SourceFiles = 1, TestFiles = 1, ConfigFiles = 0,
            DocsFiles = 0, BinaryFiles = 0, GeneratedFiles = 0,
            HighPriorityFiles = 1
        }
    };

    public GetPullRequestFilesToolHandlerTests()
    {
        _budgetResolver.Resolve(Arg.Any<int?>(), Arg.Any<string?>())
            .Returns(new BudgetResolutionResult(200000, 140000, BudgetSource.Default, Array.Empty<string>()));
        _tokenEstimator.Estimate(Arg.Any<string>(), Arg.Any<int>())
            .Returns(new TokenEstimationResult(100, 0.07, true));
        _fileClassifier.Classify(Arg.Any<string>())
            .Returns(new FileClassification { Category = FileCategory.Source, Extension = ".cs", IsBinary = false, IsGenerated = false, IsTestFile = false, ReviewPriority = "high" });

        _handler = new GetPullRequestFilesToolHandler(
            _filesProvider,
            new ResponsePacker(NullLogger<ResponsePacker>.Instance),
            _budgetResolver,
            _tokenEstimator,
            _fileClassifier,
            _pageAllocator,
            _pageReferenceCodec,
            NullLogger<GetPullRequestFilesToolHandler>.Instance);
    }

    // --- Happy path ---

    [Fact]
    public async Task ExecuteAsync_ReturnsStructuredJson()
    {
        _filesProvider.GetFilesAsync(42, Arg.Any<CancellationToken>()).Returns(SampleFiles);

        var text = await _handler.ExecuteAsync(prNumber: 42);

        var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        Assert.Equal(42, root.GetProperty("prNumber").GetInt32());
        Assert.Equal(2, root.GetProperty("totalFiles").GetInt32());
        Assert.Equal(2, root.GetProperty("files").GetArrayLength());
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsCorrectFileProperties()
    {
        _filesProvider.GetFilesAsync(42, Arg.Any<CancellationToken>()).Returns(SampleFiles);

        var text = await _handler.ExecuteAsync(prNumber: 42);
        var doc = JsonDocument.Parse(text);
        var firstFile = doc.RootElement.GetProperty("files")[0];

        Assert.Equal("src/Service.cs", firstFile.GetProperty("path").GetString());
        Assert.Equal("modified", firstFile.GetProperty("status").GetString());
        Assert.Equal(10, firstFile.GetProperty("additions").GetInt32());
        Assert.Equal(3, firstFile.GetProperty("deletions").GetInt32());
        Assert.Equal(13, firstFile.GetProperty("changes").GetInt32());
        Assert.Equal(".cs", firstFile.GetProperty("extension").GetString());
        Assert.False(firstFile.GetProperty("isBinary").GetBoolean());
        Assert.False(firstFile.GetProperty("isGenerated").GetBoolean());
        Assert.False(firstFile.GetProperty("isTestFile").GetBoolean());
        Assert.Equal("high", firstFile.GetProperty("reviewPriority").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsCorrectSummary()
    {
        _filesProvider.GetFilesAsync(42, Arg.Any<CancellationToken>()).Returns(SampleFiles);

        var text = await _handler.ExecuteAsync(prNumber: 42);
        var doc = JsonDocument.Parse(text);
        var summary = doc.RootElement.GetProperty("summary");

        Assert.Equal(1, summary.GetProperty("sourceFiles").GetInt32());
        Assert.Equal(1, summary.GetProperty("testFiles").GetInt32());
        Assert.Equal(0, summary.GetProperty("configFiles").GetInt32());
        Assert.Equal(0, summary.GetProperty("docsFiles").GetInt32());
        Assert.Equal(0, summary.GetProperty("binaryFiles").GetInt32());
        Assert.Equal(0, summary.GetProperty("generatedFiles").GetInt32());
        Assert.Equal(1, summary.GetProperty("highPriorityFiles").GetInt32());
    }

    // --- Validation errors ---

    [Fact]
    public async Task ExecuteAsync_ThrowsError_WhenPrNumberMissing()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() => _handler.ExecuteAsync());

        Assert.Contains("Missing required parameter", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsError_WhenPrNumberZero()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() => _handler.ExecuteAsync(prNumber: 0));

        Assert.Contains("greater than 0", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsError_WhenPrNumberNegative()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() => _handler.ExecuteAsync(prNumber: -1));

        Assert.Contains("greater than 0", ex.Message);
    }

    // --- Provider exceptions ---

    [Fact]
    public async Task ExecuteAsync_ThrowsError_WhenPullRequestNotFound()
    {
        _filesProvider.GetFilesAsync(999, Arg.Any<CancellationToken>())
            .ThrowsAsync(new PullRequestNotFoundException("PR #999 not found"));

        var ex = await Assert.ThrowsAsync<McpException>(() => _handler.ExecuteAsync(prNumber: 999));

        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsError_OnUnexpectedException()
    {
        _filesProvider.GetFilesAsync(42, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Something broke"));

        var ex = await Assert.ThrowsAsync<McpException>(() => _handler.ExecuteAsync(prNumber: 42));

        Assert.Contains("Something broke", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_BudgetTooSmallForPagination_ThrowsError()
    {
        _filesProvider.GetFilesAsync(42, Arg.Any<CancellationToken>()).Returns(SampleFiles);
        _budgetResolver.Resolve(Arg.Any<int?>(), Arg.Any<string?>())
            .Returns(new BudgetResolutionResult(200, 100, BudgetSource.Explicit, Array.Empty<string>()));

        _pageAllocator.Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), Arg.Any<int>())
            .Throws(new BudgetTooSmallException("Token budget (100) is too small for pagination."));

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            _handler.ExecuteAsync(prNumber: 42, maxTokens: 200));

        Assert.Contains("too small", ex.Message);
    }

    // --- Packing integration ---

    [Fact]
    public async Task ExecuteAsync_IncludesManifest_InResponse()
    {
        _filesProvider.GetFilesAsync(42, Arg.Any<CancellationToken>()).Returns(SampleFiles);

        var text = await _handler.ExecuteAsync(prNumber: 42);

        var doc = JsonDocument.Parse(text);
        Assert.True(doc.RootElement.TryGetProperty("manifest", out var manifest));
        Assert.True(manifest.TryGetProperty("summary", out var summary));
        Assert.Equal(2, summary.GetProperty("totalItems").GetInt32());
    }
}