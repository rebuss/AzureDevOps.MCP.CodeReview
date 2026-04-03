using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
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

    private static string AllText(IEnumerable<ContentBlock> blocks) =>
        string.Join("\n", blocks.Cast<TextContentBlock>().Select(b => b.Text));

    // --- Happy path ---

    [Fact]
    public async Task ExecuteAsync_SinglePage_ReturnsAllFiles()
    {
        var blocks = (await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1)).ToList();
        var text = AllText(blocks);

        Assert.Contains("src/A.cs", text);
        Assert.Contains("src/B.cs", text);
        Assert.Contains("docs/README.md", text);

        var lastBlock = blocks.Cast<TextContentBlock>().Last().Text;
        Assert.Contains("Page 1 of 1", lastBlock);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsCorrectSummary()
    {
        var blocks = (await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1)).ToList();
        var lastBlock = blocks.Cast<TextContentBlock>().Last().Text;

        Assert.Contains("Page 1 of 1", lastBlock);
        Assert.Contains("hasMore: false", lastBlock);
        Assert.Contains("3/3 files", lastBlock);
        Assert.Contains("~1500 tokens", lastBlock);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsCategories()
    {
        var blocks = (await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1)).ToList();
        var text = AllText(blocks);

        // All 3 files are classified as source, they appear in the diff output
        Assert.Contains("src/A.cs", text);
        Assert.Contains("src/B.cs", text);
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

        var blocks = (await _handler.ExecuteAsync(prNumber: 42, pageNumber: 2)).ToList();
        var lastBlock = blocks.Cast<TextContentBlock>().Last().Text;

        Assert.Contains("Page 2 of 2", lastBlock);
        Assert.Contains("hasMore: false", lastBlock);
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

        var blocks = (await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1)).ToList();
        var lastBlock = blocks.Cast<TextContentBlock>().Last().Text;

        Assert.Contains("hasMore: true", lastBlock);
    }

    [Fact]
    public async Task ExecuteAsync_FileChanges_HaveHunksAndLines()
    {
        var blocks = (await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1)).ToList();
        var text = AllText(blocks);

        Assert.Contains("src/A.cs", text);
        Assert.Contains("+new line", text);
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