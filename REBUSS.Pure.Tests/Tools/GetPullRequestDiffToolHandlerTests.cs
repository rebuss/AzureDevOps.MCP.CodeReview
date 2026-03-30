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
using REBUSS.Pure.Services.Pagination;
using REBUSS.Pure.Services.ResponsePacking;
using REBUSS.Pure.Tools;

namespace REBUSS.Pure.Tests.Tools;

public class GetPullRequestDiffToolHandlerTests
{
    private readonly IPullRequestDataProvider _diffProvider = Substitute.For<IPullRequestDataProvider>();
    private readonly IContextBudgetResolver _budgetResolver = Substitute.For<IContextBudgetResolver>();
    private readonly ITokenEstimator _tokenEstimator = Substitute.For<ITokenEstimator>();
    private readonly IFileClassifier _fileClassifier = Substitute.For<IFileClassifier>();
    private readonly IPageAllocator _pageAllocator = Substitute.For<IPageAllocator>();
    private readonly IPageReferenceCodec _pageReferenceCodec = Substitute.For<IPageReferenceCodec>();
    private readonly GetPullRequestDiffToolHandler _handler;

    private static readonly PullRequestDiff SampleDiff = new()
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

    public GetPullRequestDiffToolHandlerTests()
    {
        _budgetResolver.Resolve(Arg.Any<int?>(), Arg.Any<string?>())
            .Returns(new BudgetResolutionResult(200000, 140000, BudgetSource.Default, Array.Empty<string>()));
        _tokenEstimator.Estimate(Arg.Any<string>(), Arg.Any<int>())
            .Returns(new TokenEstimationResult(100, 0.07, true));
        _fileClassifier.Classify(Arg.Any<string>())
            .Returns(new FileClassification { Category = FileCategory.Source, Extension = ".cs", IsBinary = false, IsGenerated = false, IsTestFile = false, ReviewPriority = "high" });

        // Default F004 mocks: safe fallbacks for tests that enter pagination path
        var defaultPageSlice = new PageSlice(1, 0, 1,
            new[] { new PageSliceItem(0, PackingItemStatus.Included, 100) }, 100, 139650);
        _pageAllocator.Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), Arg.Any<int>())
            .Returns(new PageAllocation(new[] { defaultPageSlice }, 1, 1));
        _pageReferenceCodec.Encode(Arg.Any<PageReferenceData>()).Returns("default_page_ref");
        _diffProvider.GetMetadataAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new FullPullRequestMetadata { LastMergeSourceCommitId = "default_sha" });

        _handler = new GetPullRequestDiffToolHandler(
            _diffProvider,
            new ResponsePacker(NullLogger<ResponsePacker>.Instance),
            _budgetResolver,
            _tokenEstimator,
            _fileClassifier,
            _pageAllocator,
            _pageReferenceCodec,
            NullLogger<GetPullRequestDiffToolHandler>.Instance);
    }

    // --- Happy path ---

    [Fact]
    public async Task ExecuteAsync_ReturnsStructuredJson_ByDefault()
    {
        _diffProvider.GetDiffAsync(42, Arg.Any<CancellationToken>()).Returns(SampleDiff);

        var text = await _handler.ExecuteAsync(prNumber: 42);

        var doc = JsonDocument.Parse(text);
        Assert.Equal(42, doc.RootElement.GetProperty("prNumber").GetInt32());
        Assert.True(doc.RootElement.TryGetProperty("files", out var files));
        Assert.Equal(1, files.GetArrayLength());

        var file = files[0];
        Assert.Equal("/src/A.cs", file.GetProperty("path").GetString());
        Assert.Equal("edit", file.GetProperty("changeType").GetString());
        Assert.Equal(1, file.GetProperty("additions").GetInt32());
        Assert.Equal(1, file.GetProperty("deletions").GetInt32());

        Assert.True(file.TryGetProperty("hunks", out var hunks));
        Assert.Equal(1, hunks.GetArrayLength());
        var hunk = hunks[0];
        Assert.Equal(1, hunk.GetProperty("oldStart").GetInt32());
        Assert.True(hunk.TryGetProperty("lines", out var lines));
        Assert.Equal(2, lines.GetArrayLength());
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotIncludeMetadataInOutput()
    {
        _diffProvider.GetDiffAsync(42, Arg.Any<CancellationToken>()).Returns(SampleDiff);

        var text = await _handler.ExecuteAsync(prNumber: 42);

        var doc = JsonDocument.Parse(text);
        Assert.False(doc.RootElement.TryGetProperty("title", out _));
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
        var ex = await Assert.ThrowsAsync<McpException>(() => _handler.ExecuteAsync(prNumber: -5));

        Assert.Contains("greater than 0", ex.Message);
    }

    // --- Provider exceptions ---

    [Fact]
    public async Task ExecuteAsync_ThrowsError_WhenPullRequestNotFound()
    {
        _diffProvider.GetDiffAsync(999, Arg.Any<CancellationToken>())
            .ThrowsAsync(new PullRequestNotFoundException("PR #999 not found"));

        var ex = await Assert.ThrowsAsync<McpException>(() => _handler.ExecuteAsync(prNumber: 999));

        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsError_OnUnexpectedException()
    {
        _diffProvider.GetDiffAsync(42, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Something broke"));

        var ex = await Assert.ThrowsAsync<McpException>(() => _handler.ExecuteAsync(prNumber: 42));

        Assert.Contains("Something broke", ex.Message);
    }

    // --- Packing integration ---

    [Fact]
    public async Task ExecuteAsync_IncludesManifest_InResponse()
    {
        _diffProvider.GetDiffAsync(42, Arg.Any<CancellationToken>()).Returns(SampleDiff);

        var text = await _handler.ExecuteAsync(prNumber: 42);

        var doc = JsonDocument.Parse(text);
        Assert.True(doc.RootElement.TryGetProperty("manifest", out var manifest));
        Assert.True(manifest.TryGetProperty("items", out var items));
        Assert.Equal(1, items.GetArrayLength());
        Assert.True(manifest.TryGetProperty("summary", out var summary));
        Assert.Equal(1, summary.GetProperty("totalItems").GetInt32());
        Assert.Equal(1, summary.GetProperty("includedCount").GetInt32());
    }

    [Fact]
    public async Task ExecuteAsync_AcceptsOptionalModelName()
    {
        _diffProvider.GetDiffAsync(42, Arg.Any<CancellationToken>()).Returns(SampleDiff);

        var text = await _handler.ExecuteAsync(prNumber: 42, modelName: "Claude Sonnet");

        Assert.NotNull(text);
        _budgetResolver.Received(1).Resolve(Arg.Any<int?>(), "Claude Sonnet");
    }

    [Fact]
    public async Task ExecuteAsync_AcceptsOptionalMaxTokens()
    {
        _diffProvider.GetDiffAsync(42, Arg.Any<CancellationToken>()).Returns(SampleDiff);

        var text = await _handler.ExecuteAsync(prNumber: 42, maxTokens: 50000);

        Assert.NotNull(text);
        _budgetResolver.Received(1).Resolve(50000, Arg.Any<string?>());
    }

    // --- Feature 004: Pagination integration ---

    [Fact]
    public async Task ExecuteAsync_NoBudget_NoPaginationMetadata()
    {
        _diffProvider.GetDiffAsync(42, Arg.Any<CancellationToken>()).Returns(SampleDiff);

        var text = await _handler.ExecuteAsync(prNumber: 42);

        var doc = JsonDocument.Parse(text);
        Assert.False(doc.RootElement.TryGetProperty("pagination", out _));
    }

    [Fact]
    public async Task ExecuteAsync_ExplicitBudget_AllFits_SinglePage()
    {
        _diffProvider.GetDiffAsync(42, Arg.Any<CancellationToken>()).Returns(SampleDiff);
        _diffProvider.GetMetadataAsync(42, Arg.Any<CancellationToken>())
            .Returns(new FullPullRequestMetadata { LastMergeSourceCommitId = "sha123" });

        // Setup allocator to return single page
        var pageSlice = new PageSlice(1, 0, 1,
            new[] { new PageSliceItem(0, PackingItemStatus.Included, 100) },
            100, 139650);
        var allocation = new PageAllocation(new[] { pageSlice }, 1, 1);
        _pageAllocator.Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), Arg.Any<int>())
            .Returns(allocation);

        var refData = new PageReferenceData("get_pr_diff",
            JsonDocument.Parse("{\"prNumber\":42}").RootElement, 140000, 1, "sha123");
        _pageReferenceCodec.Encode(Arg.Any<PageReferenceData>()).Returns("encoded_ref");

        var text = await _handler.ExecuteAsync(prNumber: 42, maxTokens: 200000);

        var doc = JsonDocument.Parse(text);
        Assert.True(doc.RootElement.TryGetProperty("pagination", out var pagination));
        Assert.Equal(1, pagination.GetProperty("currentPage").GetInt32());
        Assert.Equal(1, pagination.GetProperty("totalPages").GetInt32());
        Assert.False(pagination.GetProperty("hasMore").GetBoolean());
        Assert.True(pagination.TryGetProperty("currentPageReference", out _));
        // nextPageReference should be absent (null → omitted)
        Assert.False(pagination.TryGetProperty("nextPageReference", out _));
    }

    [Fact]
    public async Task ExecuteAsync_ExplicitBudget_MultiPage_HasMoreTrue()
    {
        _diffProvider.GetDiffAsync(42, Arg.Any<CancellationToken>()).Returns(SampleDiff);
        _diffProvider.GetMetadataAsync(42, Arg.Any<CancellationToken>())
            .Returns(new FullPullRequestMetadata { LastMergeSourceCommitId = "sha123" });

        var page1Slice = new PageSlice(1, 0, 1,
            new[] { new PageSliceItem(0, PackingItemStatus.Included, 100) },
            100, 900);
        var page2Slice = new PageSlice(2, 1, 2,
            Array.Empty<PageSliceItem>(), 0, 0);
        var allocation = new PageAllocation(new[] { page1Slice, page2Slice }, 2, 1);
        _pageAllocator.Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), Arg.Any<int>())
            .Returns(allocation);
        _pageReferenceCodec.Encode(Arg.Any<PageReferenceData>()).Returns("encoded_ref");

        var text = await _handler.ExecuteAsync(prNumber: 42, maxTokens: 200000);

        var doc = JsonDocument.Parse(text);
        var pagination = doc.RootElement.GetProperty("pagination");
        Assert.True(pagination.GetProperty("hasMore").GetBoolean());
        Assert.True(pagination.TryGetProperty("nextPageReference", out _));
    }

    [Fact]
    public async Task ExecuteAsync_NeitherPrNumberNorPageReference_ThrowsError()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() => _handler.ExecuteAsync());

        Assert.Contains("Missing required parameter", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_BudgetTooSmallForPagination_ThrowsError()
    {
        _diffProvider.GetDiffAsync(42, Arg.Any<CancellationToken>()).Returns(SampleDiff);
        _budgetResolver.Resolve(Arg.Any<int?>(), Arg.Any<string?>())
            .Returns(new BudgetResolutionResult(200, 100, BudgetSource.Explicit, Array.Empty<string>()));

        _pageAllocator.Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), Arg.Any<int>())
            .Throws(new BudgetTooSmallException("Token budget (100) is too small for pagination."));

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            _handler.ExecuteAsync(prNumber: 42, maxTokens: 200));

        Assert.Contains("too small", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyDiffWithExplicitBudget_ReturnsEmptyPageWithPagination()
    {
        var emptyDiff = new PullRequestDiff
        {
            Title = "Empty", Status = "active", SourceBranch = "x", TargetBranch = "main",
            SourceRefName = "refs/heads/x", TargetRefName = "refs/heads/main",
            Files = new List<FileChange>()
        };
        _diffProvider.GetDiffAsync(42, Arg.Any<CancellationToken>()).Returns(emptyDiff);
        _diffProvider.GetMetadataAsync(42, Arg.Any<CancellationToken>())
            .Returns(new FullPullRequestMetadata { LastMergeSourceCommitId = "sha123" });

        var emptyPage = new PageSlice(1, 0, 0, Array.Empty<PageSliceItem>(), 0, 140000);
        var allocation = new PageAllocation(new[] { emptyPage }, 1, 0);
        _pageAllocator.Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), Arg.Any<int>())
            .Returns(allocation);
        _pageReferenceCodec.Encode(Arg.Any<PageReferenceData>()).Returns("encoded_ref");

        var text = await _handler.ExecuteAsync(prNumber: 42, maxTokens: 200000);

        var doc = JsonDocument.Parse(text);
        Assert.True(doc.RootElement.TryGetProperty("pagination", out var pagination));
        Assert.Equal(1, pagination.GetProperty("totalPages").GetInt32());
        Assert.False(pagination.GetProperty("hasMore").GetBoolean());
    }

    // --- Feature 004: Mutual exclusion ---

    [Fact]
    public async Task ExecuteAsync_BothPageReferenceAndPageNumber_ThrowsError()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() =>
            _handler.ExecuteAsync(prNumber: 42, pageReference: "some_ref", pageNumber: 2));

        Assert.Contains("Cannot specify both", ex.Message);
    }

    // --- Feature 004: Page reference resume (Phase 4) ---

    [Fact]
    public async Task ExecuteAsync_PageReference_DecodesAndServes()
    {
        var requestParams = JsonDocument.Parse("{\"prNumber\":42}").RootElement;
        _pageReferenceCodec.TryDecode("valid_ref")
            .Returns(new PageReferenceData("get_pr_diff", requestParams, 140000, 2, "sha123"));

        _diffProvider.GetDiffAsync(42, Arg.Any<CancellationToken>()).Returns(SampleDiff);
        _diffProvider.GetMetadataAsync(42, Arg.Any<CancellationToken>())
            .Returns(new FullPullRequestMetadata { LastMergeSourceCommitId = "sha123" });

        var pageSlice = new PageSlice(2, 0, 1,
            new[] { new PageSliceItem(0, PackingItemStatus.Included, 100) },
            100, 139650);
        var allocation = new PageAllocation(
            new[] {
                new PageSlice(1, 0, 0, Array.Empty<PageSliceItem>(), 0, 0),
                pageSlice
            }, 2, 1);
        _pageAllocator.Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), Arg.Any<int>())
            .Returns(allocation);
        _pageReferenceCodec.Encode(Arg.Any<PageReferenceData>()).Returns("encoded_ref");

        var text = await _handler.ExecuteAsync(pageReference: "valid_ref");

        Assert.NotNull(text);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidPageReference_ThrowsGenericError()
    {
        _pageReferenceCodec.TryDecode("bad_ref").Returns((PageReferenceData?)null);

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            _handler.ExecuteAsync(pageReference: "bad_ref"));

        Assert.Contains("Invalid page reference", ex.Message);
        // Q23: must not expose decoded internals
        Assert.DoesNotContain("field", ex.Message.ToLower());
    }

    [Fact]
    public async Task ExecuteAsync_BudgetMismatch_ThrowsError()
    {
        var requestParams = JsonDocument.Parse("{\"prNumber\":42}").RootElement;
        _pageReferenceCodec.TryDecode("ref")
            .Returns(new PageReferenceData("get_pr_diff", requestParams, 89600, 2, "sha"));

        _budgetResolver.Resolve(Arg.Any<int?>(), Arg.Any<string?>())
            .Returns(new BudgetResolutionResult(100000, 64000, BudgetSource.Explicit, Array.Empty<string>()));

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            _handler.ExecuteAsync(pageReference: "ref", maxTokens: 100000));

        Assert.Contains("Budget mismatch", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ParameterMismatch_ThrowsError()
    {
        var requestParams = JsonDocument.Parse("{\"prNumber\":42}").RootElement;
        _pageReferenceCodec.TryDecode("ref")
            .Returns(new PageReferenceData("get_pr_diff", requestParams, 140000, 2, "sha"));

        _diffProvider.GetDiffAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(SampleDiff);

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            _handler.ExecuteAsync(pageReference: "ref", prNumber: 99));

        Assert.Contains("mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // --- Feature 004: Staleness detection (Phase 5) ---

    [Fact]
    public async Task ExecuteAsync_StalenessDetected_WarningPresent()
    {
        var requestParams = JsonDocument.Parse("{\"prNumber\":42}").RootElement;
        _pageReferenceCodec.TryDecode("ref")
            .Returns(new PageReferenceData("get_pr_diff", requestParams, 140000, 1, "old_sha"));

        _diffProvider.GetDiffAsync(42, Arg.Any<CancellationToken>()).Returns(SampleDiff);
        _diffProvider.GetMetadataAsync(42, Arg.Any<CancellationToken>())
            .Returns(new FullPullRequestMetadata { LastMergeSourceCommitId = "new_sha" });

        var pageSlice = new PageSlice(1, 0, 1,
            new[] { new PageSliceItem(0, PackingItemStatus.Included, 100) },
            100, 139650);
        var allocation = new PageAllocation(new[] { pageSlice }, 1, 1);
        _pageAllocator.Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), Arg.Any<int>())
            .Returns(allocation);
        _pageReferenceCodec.Encode(Arg.Any<PageReferenceData>()).Returns("encoded_ref");

        var text = await _handler.ExecuteAsync(pageReference: "ref");

        var doc = JsonDocument.Parse(text);
        Assert.True(doc.RootElement.TryGetProperty("stalenessWarning", out var warning));
        Assert.Equal("old_sha", warning.GetProperty("originalFingerprint").GetString());
        Assert.Equal("new_sha", warning.GetProperty("currentFingerprint").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_SameSha_NoStalenessWarning()
    {
        var requestParams = JsonDocument.Parse("{\"prNumber\":42}").RootElement;
        _pageReferenceCodec.TryDecode("ref")
            .Returns(new PageReferenceData("get_pr_diff", requestParams, 140000, 1, "same_sha"));

        _diffProvider.GetDiffAsync(42, Arg.Any<CancellationToken>()).Returns(SampleDiff);
        _diffProvider.GetMetadataAsync(42, Arg.Any<CancellationToken>())
            .Returns(new FullPullRequestMetadata { LastMergeSourceCommitId = "same_sha" });

        var pageSlice = new PageSlice(1, 0, 1,
            new[] { new PageSliceItem(0, PackingItemStatus.Included, 100) },
            100, 139650);
        var allocation = new PageAllocation(new[] { pageSlice }, 1, 1);
        _pageAllocator.Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), Arg.Any<int>())
            .Returns(allocation);
        _pageReferenceCodec.Encode(Arg.Any<PageReferenceData>()).Returns("encoded_ref");

        var text = await _handler.ExecuteAsync(pageReference: "ref");

        var doc = JsonDocument.Parse(text);
        Assert.False(doc.RootElement.TryGetProperty("stalenessWarning", out _));
    }

    // --- Feature 004: Page number access (Phase 6) ---

    [Fact]
    public async Task ExecuteAsync_PageNumberDirect_ReturnsRequestedPage()
    {
        _diffProvider.GetDiffAsync(42, Arg.Any<CancellationToken>()).Returns(SampleDiff);
        _diffProvider.GetMetadataAsync(42, Arg.Any<CancellationToken>())
            .Returns(new FullPullRequestMetadata { LastMergeSourceCommitId = "sha123" });

        var page1 = new PageSlice(1, 0, 0, Array.Empty<PageSliceItem>(), 0, 0);
        var page2 = new PageSlice(2, 0, 1,
            new[] { new PageSliceItem(0, PackingItemStatus.Included, 100) },
            100, 900);
        var allocation = new PageAllocation(new[] { page1, page2 }, 2, 1);
        _pageAllocator.Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), Arg.Any<int>())
            .Returns(allocation);
        _pageReferenceCodec.Encode(Arg.Any<PageReferenceData>()).Returns("encoded_ref");

        var text = await _handler.ExecuteAsync(prNumber: 42, maxTokens: 200000, pageNumber: 2);

        var doc = JsonDocument.Parse(text);
        Assert.Equal(2, doc.RootElement.GetProperty("pagination").GetProperty("currentPage").GetInt32());
    }

    [Fact]
    public async Task ExecuteAsync_PageNumberOutOfRange_ThrowsError()
    {
        _diffProvider.GetDiffAsync(42, Arg.Any<CancellationToken>()).Returns(SampleDiff);

        var pageSlice = new PageSlice(1, 0, 1,
            new[] { new PageSliceItem(0, PackingItemStatus.Included, 100) },
            100, 139650);
        var allocation = new PageAllocation(new[] { pageSlice }, 1, 1);
        _pageAllocator.Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), Arg.Any<int>())
            .Returns(allocation);

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            _handler.ExecuteAsync(prNumber: 42, maxTokens: 200000, pageNumber: 5));

        Assert.Contains("out of range", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_PageNumberZero_ThrowsError()
    {
        _diffProvider.GetDiffAsync(42, Arg.Any<CancellationToken>()).Returns(SampleDiff);

        var pageSlice = new PageSlice(1, 0, 1,
            new[] { new PageSliceItem(0, PackingItemStatus.Included, 100) },
            100, 139650);
        var allocation = new PageAllocation(new[] { pageSlice }, 1, 1);
        _pageAllocator.Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), Arg.Any<int>())
            .Returns(allocation);

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            _handler.ExecuteAsync(prNumber: 42, maxTokens: 200000, pageNumber: 0));

        Assert.Contains("out of range", ex.Message);
    }
}