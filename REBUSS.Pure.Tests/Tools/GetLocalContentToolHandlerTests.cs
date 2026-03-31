using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using NSubstitute;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Models.Pagination;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Services.LocalReview;
using REBUSS.Pure.Tools;

namespace REBUSS.Pure.Tests.Tools;

public class GetLocalContentToolHandlerTests
{
    private readonly ILocalReviewProvider _localProvider = Substitute.For<ILocalReviewProvider>();
    private readonly IContextBudgetResolver _budgetResolver = Substitute.For<IContextBudgetResolver>();
    private readonly ITokenEstimator _tokenEstimator = Substitute.For<ITokenEstimator>();
    private readonly IFileClassifier _fileClassifier = Substitute.For<IFileClassifier>();
    private readonly IPageAllocator _pageAllocator = Substitute.For<IPageAllocator>();
    private readonly GetLocalContentToolHandler _handler;

    private static readonly LocalReviewFiles SampleLocalFiles = new()
    {
        RepositoryRoot = "C:\\Projects\\MyRepo",
        CurrentBranch = "feature/my-branch",
        Scope = "working-tree",
        Files = new List<PullRequestFileInfo>
        {
            new() { Path = "src/A.cs", Additions = 20, Deletions = 3, Changes = 23, Extension = ".cs" },
            new() { Path = "src/B.cs", Additions = 10, Deletions = 2, Changes = 12, Extension = ".cs" }
        }
    };

    private static PullRequestDiff MakeFileDiff(string path, int additions, int deletions)
    {
        return new PullRequestDiff
        {
            Files = new List<FileChange>
            {
                new()
                {
                    Path = path,
                    ChangeType = "edit",
                    Additions = additions,
                    Deletions = deletions,
                    Hunks = new List<DiffHunk>
                    {
                        new()
                        {
                            OldStart = 1, OldCount = deletions, NewStart = 1, NewCount = additions,
                            Lines = new List<DiffLine> { new() { Op = '+', Text = "new" } }
                        }
                    }
                }
            }
        };
    }

    public GetLocalContentToolHandlerTests()
    {
        _localProvider.GetFilesAsync(Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(SampleLocalFiles);
        _localProvider.GetFileDiffAsync("src/A.cs", Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(MakeFileDiff("src/A.cs", 20, 3));
        _localProvider.GetFileDiffAsync("src/B.cs", Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(MakeFileDiff("src/B.cs", 10, 2));

        _budgetResolver.Resolve(Arg.Any<int?>(), Arg.Any<string?>())
            .Returns(new BudgetResolutionResult(200000, 140000, BudgetSource.Default, Array.Empty<string>()));
        _tokenEstimator.EstimateFromStats(Arg.Any<int>(), Arg.Any<int>())
            .Returns(500);
        _fileClassifier.Classify(Arg.Any<string>())
            .Returns(new FileClassification { Category = FileCategory.Source });

        var pageSlice = new PageSlice(1, 0, 2,
            new[]
            {
                new PageSliceItem(0, PackingItemStatus.Included, 500),
                new PageSliceItem(1, PackingItemStatus.Included, 500)
            },
            1000, 139000);
        _pageAllocator.Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), Arg.Any<int>())
            .Returns(new PageAllocation(new[] { pageSlice }, 1, 2));

        _handler = new GetLocalContentToolHandler(
            _localProvider,
            _budgetResolver,
            _tokenEstimator,
            _fileClassifier,
            _pageAllocator,
            NullLogger<GetLocalContentToolHandler>.Instance);
    }

    // --- Happy path ---

    [Fact]
    public async Task ExecuteAsync_SinglePage_ReturnsAllFiles()
    {
        var json = await _handler.ExecuteAsync(pageNumber: 1);

        var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("pageNumber").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("totalPages").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("files").GetArrayLength());
    }

    [Fact]
    public async Task ExecuteAsync_IncludesRepositoryRoot()
    {
        var json = await _handler.ExecuteAsync(pageNumber: 1);

        var doc = JsonDocument.Parse(json);
        Assert.Equal("C:\\Projects\\MyRepo", doc.RootElement.GetProperty("repositoryRoot").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_IncludesCurrentBranch()
    {
        var json = await _handler.ExecuteAsync(pageNumber: 1);

        var doc = JsonDocument.Parse(json);
        Assert.Equal("feature/my-branch", doc.RootElement.GetProperty("currentBranch").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_DefaultScope_IsWorkingTree()
    {
        var json = await _handler.ExecuteAsync(pageNumber: 1);

        var doc = JsonDocument.Parse(json);
        Assert.Equal("working-tree", doc.RootElement.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_StagedScope_Forwarded()
    {
        await _handler.ExecuteAsync(pageNumber: 1, scope: "staged");

        await _localProvider.Received(1).GetFilesAsync(
            Arg.Is<LocalReviewScope>(s => s.Kind == LocalReviewScopeKind.Staged),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_BranchScope_ForwardedAsBaseBranch()
    {
        await _handler.ExecuteAsync(pageNumber: 1, scope: "main");

        await _localProvider.Received(1).GetFilesAsync(
            Arg.Is<LocalReviewScope>(s => s.Kind == LocalReviewScopeKind.BranchDiff && s.BaseBranch == "main"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_Summary_HasCorrectValues()
    {
        var json = await _handler.ExecuteAsync(pageNumber: 1);

        var doc = JsonDocument.Parse(json);
        var summary = doc.RootElement.GetProperty("summary");
        Assert.Equal(2, summary.GetProperty("filesOnPage").GetInt32());
        Assert.Equal(2, summary.GetProperty("totalFiles").GetInt32());
        Assert.Equal(1000, summary.GetProperty("estimatedTokens").GetInt32());
        Assert.False(summary.GetProperty("hasMorePages").GetBoolean());
    }

    [Fact]
    public async Task ExecuteAsync_Summary_HasCategories()
    {
        var json = await _handler.ExecuteAsync(pageNumber: 1);

        var doc = JsonDocument.Parse(json);
        var categories = doc.RootElement.GetProperty("summary").GetProperty("categories");
        Assert.Equal(2, categories.GetProperty("source").GetInt32());
    }

    [Fact]
    public async Task ExecuteAsync_OnlyFetchesDiffsForPageFiles()
    {
        var slice1 = new PageSlice(1, 0, 1,
            new[] { new PageSliceItem(0, PackingItemStatus.Included, 500) },
            500, 139500);
        var slice2 = new PageSlice(2, 1, 2,
            new[] { new PageSliceItem(1, PackingItemStatus.Included, 500) },
            500, 139500);
        _pageAllocator.Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), Arg.Any<int>())
            .Returns(new PageAllocation(new[] { slice1, slice2 }, 2, 2));

        await _handler.ExecuteAsync(pageNumber: 1);

        // Only first file's diff should be fetched
        await _localProvider.Received(1).GetFileDiffAsync(
            "src/A.cs", Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>());
        await _localProvider.DidNotReceive().GetFileDiffAsync(
            "src/B.cs", Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>());
    }

    // --- Error handling ---

    [Fact]
    public async Task ExecuteAsync_NullPageNumber_ThrowsMcpException()
    {
        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(pageNumber: null));
        Assert.Contains("pageNumber", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ZeroPageNumber_ThrowsMcpException()
    {
        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(pageNumber: 0));
        Assert.Contains("pageNumber must be >= 1", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_PageExceedsTotalPages_ThrowsMcpException()
    {
        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(pageNumber: 99));
        Assert.Contains("exceeds total pages", ex.Message);
    }

    // --- Budget forwarding ---

    [Fact]
    public async Task ExecuteAsync_ForwardsBudgetParams()
    {
        await _handler.ExecuteAsync(pageNumber: 1, modelName: "gpt-4o", maxTokens: 50000);

        _budgetResolver.Received(1).Resolve(50000, "gpt-4o");
    }

    // --- Fallback estimate for unknown line counts ---

    [Fact]
    public async Task ExecuteAsync_FileWithZeroChanges_UsesFallbackTokenEstimate()
    {
        // Arrange: file with Changes == 0 (e.g. rename without content change)
        var filesWithZeroCounts = new LocalReviewFiles
        {
            RepositoryRoot = "C:\\Projects\\MyRepo",
            CurrentBranch = "feature/my-branch",
            Scope = "working-tree",
            Files = new List<PullRequestFileInfo>
            {
                new() { Path = "src/A.cs", Additions = 0, Deletions = 0, Changes = 0, Extension = ".cs" }
            }
        };
        _localProvider.GetFilesAsync(Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(filesWithZeroCounts);
        _localProvider.GetFileDiffAsync("src/A.cs", Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(MakeFileDiff("src/A.cs", 0, 0));

        _pageAllocator.Allocate(
            Arg.Any<IReadOnlyList<PackingCandidate>>(), Arg.Any<int>())
            .Returns(call =>
            {
                var candidates = call.Arg<IReadOnlyList<PackingCandidate>>();
                var slice = new PageSlice(1, 0, 1,
                    new[] { new PageSliceItem(0, PackingItemStatus.Included, candidates[0].EstimatedTokens) },
                    candidates[0].EstimatedTokens, 139700);
                return new PageAllocation(new[] { slice }, 1, 1);
            });

        // Act
        await _handler.ExecuteAsync(pageNumber: 1);

        // Assert: candidate must use fallback (300), not EstimateFromStats(0,0) = 50
        _pageAllocator.Received(1).Allocate(
            Arg.Is<IReadOnlyList<PackingCandidate>>(list =>
                list.Count == 1 && list[0].EstimatedTokens == 300),
            Arg.Any<int>());

        _tokenEstimator.DidNotReceive().EstimateFromStats(0, 0);
    }
}
