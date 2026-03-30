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
using REBUSS.Pure.Services.LocalReview;
using REBUSS.Pure.Services.ResponsePacking;
using REBUSS.Pure.Tools;

namespace REBUSS.Pure.Tests.Tools;

public class GetLocalChangesFilesToolHandlerTests
{
    private readonly ILocalReviewProvider _reviewProvider = Substitute.For<ILocalReviewProvider>();
    private readonly IContextBudgetResolver _budgetResolver = Substitute.For<IContextBudgetResolver>();
    private readonly ITokenEstimator _tokenEstimator = Substitute.For<ITokenEstimator>();
    private readonly IFileClassifier _fileClassifier = Substitute.For<IFileClassifier>();
    private readonly IPageAllocator _pageAllocator = Substitute.For<IPageAllocator>();
    private readonly IPageReferenceCodec _pageReferenceCodec = Substitute.For<IPageReferenceCodec>();
    private readonly GetLocalChangesFilesToolHandler _handler;

    public GetLocalChangesFilesToolHandlerTests()
    {
        _budgetResolver.Resolve(Arg.Any<int?>(), Arg.Any<string?>())
            .Returns(new BudgetResolutionResult(200000, 140000, BudgetSource.Default, Array.Empty<string>()));
        _tokenEstimator.Estimate(Arg.Any<string>(), Arg.Any<int>())
            .Returns(new TokenEstimationResult(100, 0.07, true));
        _fileClassifier.Classify(Arg.Any<string>())
            .Returns(new FileClassification { Category = FileCategory.Source, Extension = ".cs", IsBinary = false, IsGenerated = false, IsTestFile = false, ReviewPriority = "high" });

        _handler = new GetLocalChangesFilesToolHandler(
            _reviewProvider,
            new ResponsePacker(NullLogger<ResponsePacker>.Instance),
            _budgetResolver,
            _tokenEstimator,
            _fileClassifier,
            _pageAllocator,
            _pageReferenceCodec,
            NullLogger<GetLocalChangesFilesToolHandler>.Instance);
    }

    private static LocalReviewFiles SampleFiles(int count = 1) => new()
    {
        RepositoryRoot = "/repo",
        Scope = "working-tree",
        CurrentBranch = "feature/x",
        Files = Enumerable.Range(0, count).Select(i => new PullRequestFileInfo
        {
            Path = $"src/File{i}.cs",
            Status = "modified",
            Additions = i,
            Deletions = i,
            Changes = i * 2,
            Extension = ".cs",
            ReviewPriority = "high"
        }).ToList(),
        Summary = new PullRequestFilesSummary { SourceFiles = count, HighPriorityFiles = count }
    };

    // --- Happy path ---

    [Fact]
    public async Task ExecuteAsync_ReturnsStructuredJson()
    {
        _reviewProvider.GetFilesAsync(Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(SampleFiles(2));

        var text = await _handler.ExecuteAsync();

        var doc = JsonDocument.Parse(text);
        Assert.Equal("/repo", doc.RootElement.GetProperty("repositoryRoot").GetString());
        Assert.Equal("working-tree", doc.RootElement.GetProperty("scope").GetString());
        Assert.Equal("feature/x", doc.RootElement.GetProperty("currentBranch").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("totalFiles").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("files").GetArrayLength());
    }

    [Fact]
    public async Task ExecuteAsync_DefaultsToWorkingTree_WhenNoScope()
    {
        _reviewProvider.GetFilesAsync(Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(SampleFiles());

        await _handler.ExecuteAsync();

        await _reviewProvider.Received(1).GetFilesAsync(
            Arg.Is<LocalReviewScope>(s => s.Kind == LocalReviewScopeKind.WorkingTree),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ParsesStagedScope()
    {
        _reviewProvider.GetFilesAsync(Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(SampleFiles());

        await _handler.ExecuteAsync(scope: "staged");

        await _reviewProvider.Received(1).GetFilesAsync(
            Arg.Is<LocalReviewScope>(s => s.Kind == LocalReviewScopeKind.Staged),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ParsesBranchDiffScope()
    {
        _reviewProvider.GetFilesAsync(Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(SampleFiles());

        await _handler.ExecuteAsync(scope: "main");

        await _reviewProvider.Received(1).GetFilesAsync(
            Arg.Is<LocalReviewScope>(s => s.Kind == LocalReviewScopeKind.BranchDiff && s.BaseBranch == "main"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsSummaryInOutput()
    {
        _reviewProvider.GetFilesAsync(Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(SampleFiles(3));

        var text = await _handler.ExecuteAsync();

        var doc = JsonDocument.Parse(text);
        var summary = doc.RootElement.GetProperty("summary");
        Assert.Equal(3, summary.GetProperty("sourceFiles").GetInt32());
    }

    // --- Error cases ---

    [Fact]
    public async Task ExecuteAsync_ThrowsError_WhenRepositoryNotFound()
    {
        _reviewProvider.GetFilesAsync(Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new LocalRepositoryNotFoundException("No repo found"));

        var ex = await Assert.ThrowsAsync<McpException>(() => _handler.ExecuteAsync());

        Assert.Contains("Repository not found", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsError_OnUnexpectedException()
    {
        _reviewProvider.GetFilesAsync(Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var ex = await Assert.ThrowsAsync<McpException>(() => _handler.ExecuteAsync());

        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_BudgetTooSmallForPagination_ThrowsError()
    {
        _reviewProvider.GetFilesAsync(Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(SampleFiles(2));
        _budgetResolver.Resolve(Arg.Any<int?>(), Arg.Any<string?>())
            .Returns(new BudgetResolutionResult(200, 100, BudgetSource.Explicit, Array.Empty<string>()));

        _pageAllocator.Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), Arg.Any<int>())
            .Throws(new BudgetTooSmallException("Token budget (100) is too small for pagination."));

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            _handler.ExecuteAsync(maxTokens: 200));

        Assert.Contains("too small", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsError_WhenGitCommandFails()
    {
        _reviewProvider.GetFilesAsync(Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new GitCommandException(128, "fatal: not a git repository"));

        var ex = await Assert.ThrowsAsync<McpException>(() => _handler.ExecuteAsync());

        Assert.Contains("Git command failed", ex.Message);
    }

    // --- Packing integration ---

    [Fact]
    public async Task ExecuteAsync_IncludesManifest_InResponse()
    {
        _reviewProvider.GetFilesAsync(Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(SampleFiles(2));

        var text = await _handler.ExecuteAsync();

        var doc = JsonDocument.Parse(text);
        Assert.True(doc.RootElement.TryGetProperty("manifest", out var manifest));
        Assert.True(manifest.TryGetProperty("summary", out var summary));
        Assert.Equal(2, summary.GetProperty("totalItems").GetInt32());
    }
}