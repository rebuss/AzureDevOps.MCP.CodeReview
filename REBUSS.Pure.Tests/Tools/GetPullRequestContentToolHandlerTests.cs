using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Models.Pagination;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Tools;

namespace REBUSS.Pure.Tests.Tools;

public class GetPullRequestContentToolHandlerTests
{
    private readonly IPullRequestDiffCache _diffCache = Substitute.For<IPullRequestDiffCache>();
    private readonly IContextBudgetResolver _budgetResolver = Substitute.For<IContextBudgetResolver>();
    private readonly ITokenEstimator _tokenEstimator = Substitute.For<ITokenEstimator>();
    private readonly IFileClassifier _fileClassifier = Substitute.For<IFileClassifier>();
    private readonly IPageAllocator _pageAllocator = Substitute.For<IPageAllocator>();
    private readonly GetPullRequestContentToolHandler _handler;

    private static readonly PullRequestDiff SampleDiff = new()
    {
        Title = "Fix bug",
        Status = "active",
        SourceBranch = "feature/x",
        TargetBranch = "main",
        Files = new List<FileChange>
        {
            new()
            {
                Path = "src/A.cs", ChangeType = "edit", Additions = 30, Deletions = 5,
                Hunks = new List<DiffHunk>
                {
                    new()
                    {
                        OldStart = 1, OldCount = 5, NewStart = 1, NewCount = 10,
                        Lines = new List<DiffLine> { new() { Op = '+', Text = "new line" } }
                    }
                }
            },
            new()
            {
                Path = "src/B.cs", ChangeType = "edit", Additions = 20, Deletions = 5,
                Hunks = new List<DiffHunk>
                {
                    new()
                    {
                        OldStart = 1, OldCount = 5, NewStart = 1, NewCount = 10,
                        Lines = new List<DiffLine> { new() { Op = '+', Text = "another line" } }
                    }
                }
            },
            new()
            {
                Path = "docs/README.md", ChangeType = "edit", Additions = 3, Deletions = 1,
                Hunks = new List<DiffHunk>
                {
                    new()
                    {
                        OldStart = 1, OldCount = 1, NewStart = 1, NewCount = 3,
                        Lines = new List<DiffLine> { new() { Op = '+', Text = "doc line" } }
                    }
                }
            }
        }
    };

    public GetPullRequestContentToolHandlerTests()
    {
        _diffCache.GetOrFetchDiffAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(SampleDiff);

        _budgetResolver.Resolve(Arg.Any<int?>(), Arg.Any<string?>())
            .Returns(new BudgetResolutionResult(200000, 140000, BudgetSource.Default, Array.Empty<string>()));
        _tokenEstimator.EstimateTokenCount(Arg.Any<string>())
            .Returns(500);
        _fileClassifier.Classify(Arg.Any<string>())
            .Returns(new FileClassification { Category = FileCategory.Source });

        // Default: all 3 files on page 1
        var pageSlice = new PageSlice(1, 0, 3,
            new[]
            {
                new PageSliceItem(0, PackingItemStatus.Included, 500),
                new PageSliceItem(1, PackingItemStatus.Included, 500),
                new PageSliceItem(2, PackingItemStatus.Included, 500)
            },
            1500, 138500);
        _pageAllocator.Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), Arg.Any<int>())
            .Returns(new PageAllocation(new[] { pageSlice }, 1, 3));

        _handler = new GetPullRequestContentToolHandler(
            _diffCache,
            _budgetResolver,
            _tokenEstimator,
            _fileClassifier,
            _pageAllocator,
            NullLogger<GetPullRequestContentToolHandler>.Instance);
    }

    // --- Happy path ---

    [Fact]
    public async Task ExecuteAsync_SinglePage_ReturnsAllFiles()
    {
        var json = await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1);

        var doc = JsonDocument.Parse(json);
        Assert.Equal(42, doc.RootElement.GetProperty("prNumber").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("pageNumber").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("totalPages").GetInt32());
        Assert.Equal(3, doc.RootElement.GetProperty("files").GetArrayLength());
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsCorrectSummary()
    {
        var json = await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1);

        var doc = JsonDocument.Parse(json);
        var summary = doc.RootElement.GetProperty("summary");
        Assert.Equal(3, summary.GetProperty("filesOnPage").GetInt32());
        Assert.Equal(3, summary.GetProperty("totalFiles").GetInt32());
        Assert.Equal(1500, summary.GetProperty("estimatedTokens").GetInt32());
        Assert.False(summary.GetProperty("hasMorePages").GetBoolean());
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsCategories()
    {
        var json = await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1);

        var doc = JsonDocument.Parse(json);
        var categories = doc.RootElement.GetProperty("summary").GetProperty("categories");
        Assert.Equal(3, categories.GetProperty("source").GetInt32());
    }

    [Fact]
    public async Task ExecuteAsync_MultiplePages_ReturnsOnlyRequestedPage()
    {
        var slice1 = new PageSlice(1, 0, 2,
            new[]
            {
                new PageSliceItem(0, PackingItemStatus.Included, 500),
                new PageSliceItem(1, PackingItemStatus.Included, 500)
            },
            1000, 139000);
        var slice2 = new PageSlice(2, 2, 3,
            new[] { new PageSliceItem(2, PackingItemStatus.Included, 500) },
            500, 139500);
        _pageAllocator.Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), Arg.Any<int>())
            .Returns(new PageAllocation(new[] { slice1, slice2 }, 2, 3));

        var json = await _handler.ExecuteAsync(prNumber: 42, pageNumber: 2);

        var doc = JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetProperty("pageNumber").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("totalPages").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("files").GetArrayLength());
        Assert.True(doc.RootElement.GetProperty("summary").GetProperty("hasMorePages").GetBoolean() == false);
    }

    [Fact]
    public async Task ExecuteAsync_HasMorePages_TrueWhenNotLastPage()
    {
        var slice1 = new PageSlice(1, 0, 2,
            new[]
            {
                new PageSliceItem(0, PackingItemStatus.Included, 500),
                new PageSliceItem(1, PackingItemStatus.Included, 500)
            },
            1000, 139000);
        var slice2 = new PageSlice(2, 2, 3,
            new[] { new PageSliceItem(2, PackingItemStatus.Included, 500) },
            500, 139500);
        _pageAllocator.Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), Arg.Any<int>())
            .Returns(new PageAllocation(new[] { slice1, slice2 }, 2, 3));

        var json = await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1);

        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("summary").GetProperty("hasMorePages").GetBoolean());
    }

    [Fact]
    public async Task ExecuteAsync_FileChanges_HaveHunksAndLines()
    {
        var json = await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1);

        var doc = JsonDocument.Parse(json);
        var firstFile = doc.RootElement.GetProperty("files")[0];
        Assert.Equal("src/A.cs", firstFile.GetProperty("path").GetString());
        Assert.True(firstFile.GetProperty("hunks").GetArrayLength() > 0);
        var firstHunk = firstFile.GetProperty("hunks")[0];
        Assert.True(firstHunk.GetProperty("lines").GetArrayLength() > 0);
    }

    // --- Error handling ---

    [Fact]
    public async Task ExecuteAsync_NullPrNumber_ThrowsMcpException()
    {
        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(prNumber: null, pageNumber: 1));
        Assert.Contains("prNumber", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_NullPageNumber_ThrowsMcpException()
    {
        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(prNumber: 42, pageNumber: null));
        Assert.Contains("pageNumber", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ZeroPageNumber_ThrowsMcpException()
    {
        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(prNumber: 42, pageNumber: 0));
        Assert.Contains("pageNumber must be >= 1", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_PageExceedsTotalPages_ThrowsMcpException()
    {
        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(prNumber: 42, pageNumber: 99));
        Assert.Contains("exceeds total pages", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_PrNotFound_ThrowsMcpException()
    {
        _diffCache.GetOrFetchDiffAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new PullRequestNotFoundException("Not found"));

        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(prNumber: 999, pageNumber: 1));
        Assert.Contains("Pull Request not found", ex.Message);
    }

    // --- Budget forwarding ---

    [Fact]
    public async Task ExecuteAsync_ForwardsBudgetParams()
    {
        await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1, modelName: "gpt-4o", maxTokens: 50000);

        _budgetResolver.Received(1).Resolve(50000, "gpt-4o");
    }

    // --- Diff-based measurement ---

    [Fact]
    public async Task ExecuteAsync_UsesDiffCacheForFileData()
    {
        await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1);

        await _diffCache.Received(1).GetOrFetchDiffAsync(42, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_CallsEstimateTokenCountForEachFile()
    {
        await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1);

        _tokenEstimator.Received(3).EstimateTokenCount(Arg.Any<string>());
    }
}
