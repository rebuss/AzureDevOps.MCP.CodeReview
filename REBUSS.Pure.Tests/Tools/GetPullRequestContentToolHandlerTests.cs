using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Models.Pagination;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Core.Services.CopilotReview;
using REBUSS.Pure.Services.PrEnrichment;
using REBUSS.Pure.Services.ResponsePacking;
using REBUSS.Pure.Tools;

namespace REBUSS.Pure.Tests.Tools;

public class GetPullRequestContentToolHandlerTests
{
    private readonly IPullRequestDataProvider _metadataProvider = Substitute.For<IPullRequestDataProvider>();
    private readonly IContextBudgetResolver _budgetResolver = Substitute.For<IContextBudgetResolver>();
    private readonly IPrEnrichmentOrchestrator _orchestrator = Substitute.For<IPrEnrichmentOrchestrator>();
    private readonly IPageAllocator _pageAllocator = Substitute.For<IPageAllocator>();
    private readonly IOptions<WorkflowOptions> _workflowOptions =
        Options.Create(new WorkflowOptions { MetadataInternalTimeoutMs = 28_000, ContentInternalTimeoutMs = 28_000 });
    // Feature 013 defaults: Copilot is NOT available so existing tests exercise the
    // content-only path unchanged. Tests that want the copilot-assisted branch override.
    private readonly ICopilotAvailabilityDetector _copilotAvailability = Substitute.For<ICopilotAvailabilityDetector>();
    private readonly ICopilotReviewOrchestrator _copilotReviewOrchestrator = Substitute.For<ICopilotReviewOrchestrator>();
    private readonly GetPullRequestContentToolHandler _handler;

    private static readonly FullPullRequestMetadata SampleMetadata = new()
    {
        PullRequestId = 42,
        Title = "Fix bug",
        Status = "active",
        LastMergeSourceCommitId = "abc123",
        LastMergeTargetCommitId = "def456",
        CommitShas = new List<string> { "abc123" },
    };

    private static PrEnrichmentResult BuildResult(
        int safeBudget = 140_000,
        int totalPages = 1,
        int totalItems = 3)
    {
        var candidates = new[]
        {
            new PackingCandidate("src/A.cs", 500, FileCategory.Source, 35),
            new PackingCandidate("src/B.cs", 500, FileCategory.Source, 25),
            new PackingCandidate("docs/README.md", 500, FileCategory.Docs, 4),
        };
        var enrichedByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["src/A.cs"] = "=== src/A.cs ===\n+new line",
            ["src/B.cs"] = "=== src/B.cs ===\n+another line",
            ["docs/README.md"] = "=== docs/README.md ===\n+doc line",
        };

        PageAllocation allocation;
        if (totalPages == 1)
        {
            var slice = new PageSlice(1, 0, totalItems,
                Enumerable.Range(0, totalItems).Select(i => new PageSliceItem(i, PackingItemStatus.Included, 500)).ToArray(),
                500 * totalItems, safeBudget - 500 * totalItems);
            allocation = new PageAllocation(new[] { slice }, 1, totalItems);
        }
        else
        {
            var slice1 = new PageSlice(1, 0, 2,
                new[] { new PageSliceItem(0, PackingItemStatus.Included, 500), new PageSliceItem(1, PackingItemStatus.Included, 500) },
                1000, safeBudget - 1000);
            var slice2 = new PageSlice(2, 2, 3,
                new[] { new PageSliceItem(2, PackingItemStatus.Included, 500) },
                500, safeBudget - 500);
            allocation = new PageAllocation(new[] { slice1, slice2 }, 2, 3);
        }

        return new PrEnrichmentResult
        {
            PrNumber = 42,
            HeadSha = "abc123",
            SortedCandidates = candidates,
            EnrichedByPath = enrichedByPath,
            Allocation = allocation,
            SafeBudgetTokens = safeBudget,
            CompletedAt = DateTimeOffset.UtcNow,
        };
    }

    public GetPullRequestContentToolHandlerTests()
    {
        _metadataProvider.GetMetadataAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(SampleMetadata);
        _budgetResolver.Resolve(Arg.Any<int?>(), Arg.Any<string?>())
            .Returns(new BudgetResolutionResult(200_000, 140_000, BudgetSource.Default, Array.Empty<string>()));

        // Default: no prior snapshot — handler will trigger enrichment, then await.
        _orchestrator.TryGetSnapshot(Arg.Any<int>()).Returns((PrEnrichmentJobSnapshot?)null);
        var defaultResult = BuildResult();
        _orchestrator.WaitForEnrichmentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(defaultResult);
        // The handler now repaginates per-call. Default mock: echo back the cached
        // single-page allocation so existing tests stay green.
        _pageAllocator.Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), Arg.Any<int>())
            .Returns(defaultResult.Allocation);

        // Default: Copilot not available — existing content-only tests see no behavior change
        // beyond the new [review-mode: content-only] prefix block.
        _copilotAvailability.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(false);

        _handler = new GetPullRequestContentToolHandler(
            _metadataProvider,
            _budgetResolver,
            _orchestrator,
            _pageAllocator,
            _workflowOptions,
            _copilotAvailability,
            _copilotReviewOrchestrator,
            NullLogger<GetPullRequestContentToolHandler>.Instance);
    }

    private static string AllText(IEnumerable<ContentBlock> blocks) =>
        string.Join("\n", blocks.Cast<TextContentBlock>().Select(b => b.Text));

    // ─── Cold-start path: no prior snapshot ───────────────────────────────────

    [Fact]
    public async Task Execute_NoSnapshot_TriggersAndAwaits()
    {
        var blocks = (await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1)).ToList();
        var text = AllText(blocks);

        await _metadataProvider.Received(1).GetMetadataAsync(42, Arg.Any<CancellationToken>());
        _orchestrator.Received(1).TriggerEnrichment(42, "abc123", 140_000);
        await _orchestrator.Received(1).WaitForEnrichmentAsync(42, Arg.Any<CancellationToken>());
        Assert.Contains("src/A.cs", text);
    }

    // ─── Feature 013 copilot-assisted branch tests (T029, T033) ──────────────────

    [Fact]
    public async Task Execute_CopilotNotAvailable_ReturnsContentWithContentOnlyHeader()
    {
        // Default from fixture: _copilotAvailability returns false.
        var blocks = (await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1)).ToList();
        var text = AllText(blocks);

        Assert.Contains("[review-mode: content-only]", text);
        Assert.DoesNotContain("[review-mode: copilot-assisted]", text);
        Assert.Contains("src/A.cs", text); // existing content still present
        _copilotReviewOrchestrator.DidNotReceive().TriggerReview(Arg.Any<int>(), Arg.Any<object>());
    }

    [Fact]
    public async Task Execute_CopilotAvailable_ReturnsReviewSummariesWithModeHeader()
    {
        _copilotAvailability.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(true);
        var copilotResult = new Core.Models.CopilotReview.CopilotReviewResult
        {
            PrNumber = 42,
            PageReviews = new[]
            {
                Core.Models.CopilotReview.CopilotPageReviewResult.Success(1, "no issues found", 1),
                Core.Models.CopilotReview.CopilotPageReviewResult.Success(2, "minor: fix typo in comment", 1),
            },
            CompletedAt = DateTimeOffset.UtcNow,
        };
        _copilotReviewOrchestrator
            .WaitForReviewAsync(42, Arg.Any<CancellationToken>())
            .Returns(copilotResult);

        var blocks = (await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1)).ToList();
        var text = AllText(blocks);

        Assert.Contains("[review-mode: copilot-assisted]", text);
        Assert.Contains("PR #42", text);
        Assert.Contains("2 pages reviewed", text);
        Assert.Contains("2 succeeded", text);
        Assert.Contains("=== Page 1 Review ===", text);
        Assert.Contains("no issues found", text);
        Assert.Contains("=== Page 2 Review ===", text);
        Assert.DoesNotContain("[review-mode: content-only]", text);
        _copilotReviewOrchestrator.Received(1).TriggerReview(42, Arg.Any<object>());
    }

    [Fact]
    public async Task Execute_PageNumberIgnoredInCopilotMode_ReturnsAllSummaries()
    {
        _copilotAvailability.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(true);
        var copilotResult = new Core.Models.CopilotReview.CopilotReviewResult
        {
            PrNumber = 42,
            PageReviews = new[]
            {
                Core.Models.CopilotReview.CopilotPageReviewResult.Success(1, "page 1 review", 1),
                Core.Models.CopilotReview.CopilotPageReviewResult.Success(2, "page 2 review", 1),
                Core.Models.CopilotReview.CopilotPageReviewResult.Success(3, "page 3 review", 1),
            },
            CompletedAt = DateTimeOffset.UtcNow,
        };
        _copilotReviewOrchestrator
            .WaitForReviewAsync(42, Arg.Any<CancellationToken>())
            .Returns(copilotResult);

        // Caller requests pageNumber=2 — should be ignored, all pages returned in copilot mode.
        var blocks = (await _handler.ExecuteAsync(prNumber: 42, pageNumber: 2)).ToList();
        var text = AllText(blocks);

        Assert.Contains("page 1 review", text);
        Assert.Contains("page 2 review", text);
        Assert.Contains("page 3 review", text);
    }

    // ─── Feature 013 Phase 5 US3 (T038) — partial + all-failed tool handler tests ──

    [Fact]
    public async Task Execute_CopilotPartialFailure_ResponseIncludesFailureBlocksWithFilePaths()
    {
        _copilotAvailability.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(true);
        var copilotResult = new Core.Models.CopilotReview.CopilotReviewResult
        {
            PrNumber = 42,
            PageReviews = new[]
            {
                Core.Models.CopilotReview.CopilotPageReviewResult.Success(1, "all good on page 1", 1),
                Core.Models.CopilotReview.CopilotPageReviewResult.Failure(
                    2, new[] { "src/Failing.cs", "src/AlsoFailing.cs" }, "network timeout", 3),
            },
            CompletedAt = DateTimeOffset.UtcNow,
        };
        _copilotReviewOrchestrator
            .WaitForReviewAsync(42, Arg.Any<CancellationToken>())
            .Returns(copilotResult);

        var blocks = (await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1)).ToList();
        var text = AllText(blocks);

        Assert.Contains("[review-mode: copilot-assisted]", text);
        Assert.Contains("2 pages reviewed", text);
        Assert.Contains("1 succeeded", text);
        Assert.Contains("1 failed", text);
        Assert.Contains("=== Page 1 Review ===", text);
        Assert.Contains("all good on page 1", text);
        Assert.Contains("=== Page 2 Review (FAILED) ===", text);
        Assert.Contains("src/Failing.cs", text);
        Assert.Contains("src/AlsoFailing.cs", text);
        Assert.Contains("network timeout", text);
    }

    [Fact]
    public async Task Execute_CopilotAllPagesFailed_ResponseStillReturnsCopilotAssistedHeader()
    {
        // FR-008: when every page fails, the response still carries the copilot-assisted
        // mode indicator (not content-only) — the copilot path did run, just produced zero
        // successes. The IDE agent's copilot-assisted branch is what surfaces the file paths.
        _copilotAvailability.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(true);
        var copilotResult = new Core.Models.CopilotReview.CopilotReviewResult
        {
            PrNumber = 42,
            PageReviews = new[]
            {
                Core.Models.CopilotReview.CopilotPageReviewResult.Failure(
                    1, new[] { "src/A.cs" }, "down", 3),
                Core.Models.CopilotReview.CopilotPageReviewResult.Failure(
                    2, new[] { "src/B.cs" }, "down", 3),
            },
            CompletedAt = DateTimeOffset.UtcNow,
        };
        _copilotReviewOrchestrator
            .WaitForReviewAsync(42, Arg.Any<CancellationToken>())
            .Returns(copilotResult);

        var blocks = (await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1)).ToList();
        var text = AllText(blocks);

        Assert.Contains("[review-mode: copilot-assisted]", text);
        Assert.DoesNotContain("[review-mode: content-only]", text);
        Assert.Contains("0 succeeded", text);
        Assert.Contains("2 failed", text);
        Assert.Contains("src/A.cs", text);
        Assert.Contains("src/B.cs", text);
    }

    [Fact]
    public async Task Execute_NoSnapshot_ReturnsAllFilesOnSinglePage()
    {
        var blocks = (await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1)).ToList();
        var text = AllText(blocks);

        Assert.Contains("src/A.cs", text);
        Assert.Contains("src/B.cs", text);
        Assert.Contains("docs/README.md", text);
        var lastBlock = blocks.Cast<TextContentBlock>().Last().Text;
        Assert.Contains("Page 1 of 1", lastBlock);
    }

    // ─── Warm path: snapshot already Ready ────────────────────────────────────

    [Fact]
    public async Task Execute_SnapshotReady_ServesPageWithoutNewMetadataLookup()
    {
        var result = BuildResult();
        _orchestrator.TryGetSnapshot(42).Returns(new PrEnrichmentJobSnapshot
        {
            PrNumber = 42,
            HeadSha = "abc123",
            Status = PrEnrichmentStatus.Ready,
            Result = result,
        });
        // WaitForEnrichmentAsync still has to return the same Ready result.
        _orchestrator.WaitForEnrichmentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(result);

        var blocks = (await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1)).ToList();
        var text = AllText(blocks);

        // Cold-start metadata lookup should NOT happen on warm path.
        await _metadataProvider.DidNotReceiveWithAnyArgs().GetMetadataAsync(0, default);
        // No retrigger when budget matches.
        _orchestrator.DidNotReceiveWithAnyArgs().TriggerEnrichment(0, null!, 0);
        Assert.Contains("src/A.cs", text);
    }

    [Fact]
    public async Task Execute_BudgetMismatch_RepaginatesWithoutRetriggeringEnrichment()
    {
        // Cached enrichment was primed at safeBudget = 70_000; caller now asks for 140_000.
        // The handler MUST NOT retrigger enrichment — it must reuse the cached candidates and
        // run a fresh pagination at the new budget.
        var staleResult = BuildResult(safeBudget: 70_000);
        _orchestrator.TryGetSnapshot(42).Returns(new PrEnrichmentJobSnapshot
        {
            PrNumber = 42,
            HeadSha = "abc123",
            Status = PrEnrichmentStatus.Ready,
            Result = staleResult,
        });
        _orchestrator.WaitForEnrichmentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(staleResult);
        _pageAllocator.Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), Arg.Any<int>())
            .Returns(staleResult.Allocation);

        await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1, modelName: "gpt-4o", maxTokens: 140_000);

        _orchestrator.DidNotReceiveWithAnyArgs().TriggerEnrichment(0, null!, 0);
        _pageAllocator.Received().Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), 140_000);
    }

    [Fact]
    public async Task Execute_TwoCallsDifferentBudgets_EnrichmentRunsOnce_AllocatorRunsTwice()
    {
        // Bug repro: priming enrichment at one budget then asking for content at a smaller
        // budget must yield a fresh pagination, not the cached one — and must NOT re-enrich.
        var cachedResult = BuildResult(safeBudget: 140_000);
        _orchestrator.TryGetSnapshot(42).Returns(
            (PrEnrichmentJobSnapshot?)null,
            new PrEnrichmentJobSnapshot
            {
                PrNumber = 42,
                HeadSha = "abc123",
                Status = PrEnrichmentStatus.Ready,
                Result = cachedResult,
            });
        _orchestrator.WaitForEnrichmentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(cachedResult);

        _budgetResolver.Resolve(140_000, null).Returns(
            new BudgetResolutionResult(200_000, 140_000, BudgetSource.Explicit, Array.Empty<string>()));
        _budgetResolver.Resolve(8_000, null).Returns(
            new BudgetResolutionResult(8_000, 5_600, BudgetSource.Explicit, Array.Empty<string>()));

        // Two distinct allocations to assert that the per-call budget actually drives pagination.
        var bigAlloc = BuildResult(safeBudget: 140_000, totalPages: 1, totalItems: 3).Allocation;
        var smallAlloc = BuildResult(safeBudget: 5_600, totalPages: 2, totalItems: 3).Allocation;
        _pageAllocator.Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), 140_000).Returns(bigAlloc);
        _pageAllocator.Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), 5_600).Returns(smallAlloc);

        // Call 1: cold start with the large budget (primes the cache).
        await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1, maxTokens: 140_000);
        // Call 2: warm path with a much smaller budget — must NOT retrigger.
        await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1, maxTokens: 8_000);

        // Cold start triggered exactly once; warm path did not retrigger.
        _orchestrator.Received(1).TriggerEnrichment(42, "abc123", Arg.Any<int>());
        // Allocator ran for both budgets.
        _pageAllocator.Received(1).Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), 140_000);
        _pageAllocator.Received(1).Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), 5_600);
    }

    // ─── Wait timeout — friendly status ───────────────────────────────────────

    [Fact]
    public async Task Execute_WaitTimesOut_ReturnsFriendlyStatusBlock()
    {
        _orchestrator.WaitForEnrichmentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<PrEnrichmentResult>>(_ => throw new OperationCanceledException());

        var blocks = (await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1)).ToList();
        var text = AllText(blocks);

        Assert.Single(blocks);
        Assert.Contains("Status: Response is still being prepared", text);
        Assert.Contains("PR #42", text);
        Assert.Contains("Retry get_pr_content with pageNumber=1", text);
    }

    // ─── Failed snapshot — friendly status, no retrigger ──────────────────────

    [Fact]
    public async Task Execute_FailedSnapshot_ReturnsFriendlyFailureBlock()
    {
        var failure = new PrEnrichmentFailure
        {
            ExceptionTypeName = "BoomException",
            SanitizedMessage = "diff fetch exploded",
            FailedAt = DateTimeOffset.UtcNow,
        };
        _orchestrator.TryGetSnapshot(42).Returns(new PrEnrichmentJobSnapshot
        {
            PrNumber = 42,
            HeadSha = "abc123",
            Status = PrEnrichmentStatus.Failed,
            Failure = failure,
        });

        var blocks = (await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1)).ToList();
        var text = AllText(blocks);

        Assert.Single(blocks);
        Assert.Contains("Background enrichment failed", text);
        Assert.Contains("BoomException", text);
        Assert.Contains("diff fetch exploded", text);
        // No retrigger and no metadata fetch on Failed fast-path.
        _orchestrator.DidNotReceiveWithAnyArgs().TriggerEnrichment(0, null!, 0);
        await _metadataProvider.DidNotReceiveWithAnyArgs().GetMetadataAsync(0, default);
    }

    // ─── Multipage paging ─────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_MultiPage_ReturnsRequestedPage()
    {
        var multi = BuildResult(totalPages: 2, totalItems: 3);
        _orchestrator.WaitForEnrichmentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(multi);
        _pageAllocator.Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), Arg.Any<int>())
            .Returns(multi.Allocation);

        var blocks = (await _handler.ExecuteAsync(prNumber: 42, pageNumber: 2)).ToList();
        var lastBlock = blocks.Cast<TextContentBlock>().Last().Text;

        Assert.Contains("Page 2 of 2", lastBlock);
        Assert.Contains("hasMore: false", lastBlock);
    }

    [Fact]
    public async Task Execute_MultiPage_HasMoreOnFirstPage()
    {
        var multi = BuildResult(totalPages: 2, totalItems: 3);
        _orchestrator.WaitForEnrichmentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(multi);
        _pageAllocator.Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), Arg.Any<int>())
            .Returns(multi.Allocation);

        var blocks = (await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1)).ToList();
        var lastBlock = blocks.Cast<TextContentBlock>().Last().Text;

        Assert.Contains("hasMore: true", lastBlock);
    }

    // ─── Validation + error handling ──────────────────────────────────────────

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
        _metadataProvider.GetMetadataAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new PullRequestNotFoundException("Not found"));

        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(prNumber: 999, pageNumber: 1));
        Assert.Contains("Pull Request not found", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ForwardsBudgetParams()
    {
        await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1, modelName: "gpt-4o", maxTokens: 50_000);

        _budgetResolver.Received(1).Resolve(50_000, "gpt-4o");
    }
}
