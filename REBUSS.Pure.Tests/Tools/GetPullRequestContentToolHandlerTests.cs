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
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Services.PrEnrichment;
using REBUSS.Pure.Services.ResponsePacking;
using REBUSS.Pure.Tools;
using REBUSS.Pure.Services.CopilotReview;
using CopilotUnavailableException = global::REBUSS.Pure.Services.CopilotReview.CopilotUnavailableException;

namespace REBUSS.Pure.Tests.Tools;

public class GetPullRequestContentToolHandlerTests
{
    private readonly IPullRequestDataProvider _metadataProvider = Substitute.For<IPullRequestDataProvider>();
    private readonly IContextBudgetResolver _budgetResolver = Substitute.For<IContextBudgetResolver>();
    private readonly IPrEnrichmentOrchestrator _orchestrator = Substitute.For<IPrEnrichmentOrchestrator>();
    private readonly IPageAllocator _pageAllocator = Substitute.For<IPageAllocator>();
    private readonly IOptions<WorkflowOptions> _workflowOptions =
        Options.Create(new WorkflowOptions
        {
            MetadataInternalTimeoutMs = 28_000,
            ContentInternalTimeoutMs = 28_000,
            CopilotReviewProgressPollingIntervalMs = 50,
        });
    // Feature 013 defaults: Copilot is NOT available so existing tests exercise the
    // content-only path unchanged. Tests that want the copilot-assisted branch override.
    private readonly ICopilotAvailabilityDetector _copilotAvailability = Substitute.For<ICopilotAvailabilityDetector>();
    private readonly ICopilotReviewOrchestrator _copilotReviewOrchestrator = Substitute.For<ICopilotReviewOrchestrator>();
    private readonly IProgressReporter _progressReporter = Substitute.For<IProgressReporter>();
    private readonly CopilotReviewWaiter _copilotReviewWaiter;
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

        _copilotReviewWaiter = new CopilotReviewWaiter(
            _copilotReviewOrchestrator,
            _progressReporter,
            _workflowOptions);

        _handler = new GetPullRequestContentToolHandler(
            _metadataProvider,
            _budgetResolver,
            _orchestrator,
            _pageAllocator,
            _workflowOptions,
            _copilotAvailability,
            _copilotReviewOrchestrator,
            _copilotReviewWaiter,
            _progressReporter,
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
        _copilotReviewOrchestrator.DidNotReceive().TriggerReview(Arg.Any<string>(), Arg.Any<IEnrichmentResult>());
    }

    [Fact]
    public async Task Execute_CopilotAvailable_ReturnsReviewSummariesWithModeHeader()
    {
        _copilotAvailability.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(true);
        var copilotResult = new Core.Models.CopilotReview.CopilotReviewResult
        {
            ReviewKey = "pr:42",
            PageReviews = new[]
            {
                Core.Models.CopilotReview.CopilotPageReviewResult.Success(1, "no issues found", 1),
                Core.Models.CopilotReview.CopilotPageReviewResult.Success(2, "minor: fix typo in comment", 1),
            },
            CompletedAt = DateTimeOffset.UtcNow,
        };
        _copilotReviewOrchestrator
            .WaitForReviewAsync("pr:42", Arg.Any<CancellationToken>())
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
        _copilotReviewOrchestrator.Received(1).TriggerReview("pr:42", Arg.Any<IEnrichmentResult>());
    }

    [Fact]
    public async Task Execute_PageNumberIgnoredInCopilotMode_ReturnsAllSummaries()
    {
        _copilotAvailability.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(true);
        var copilotResult = new Core.Models.CopilotReview.CopilotReviewResult
        {
            ReviewKey = "pr:42",
            PageReviews = new[]
            {
                Core.Models.CopilotReview.CopilotPageReviewResult.Success(1, "page 1 review", 1),
                Core.Models.CopilotReview.CopilotPageReviewResult.Success(2, "page 2 review", 1),
                Core.Models.CopilotReview.CopilotPageReviewResult.Success(3, "page 3 review", 1),
            },
            CompletedAt = DateTimeOffset.UtcNow,
        };
        _copilotReviewOrchestrator
            .WaitForReviewAsync("pr:42", Arg.Any<CancellationToken>())
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
            ReviewKey = "pr:42",
            PageReviews = new[]
            {
                Core.Models.CopilotReview.CopilotPageReviewResult.Success(1, "all good on page 1", 1),
                Core.Models.CopilotReview.CopilotPageReviewResult.Failure(
                    2, new[] { "src/Failing.cs", "src/AlsoFailing.cs" }, "network timeout", 3),
            },
            CompletedAt = DateTimeOffset.UtcNow,
        };
        _copilotReviewOrchestrator
            .WaitForReviewAsync("pr:42", Arg.Any<CancellationToken>())
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
            ReviewKey = "pr:42",
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
            .WaitForReviewAsync("pr:42", Arg.Any<CancellationToken>())
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

    // ─── Feature 017: Progress notifications ─────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ReportsProgress_AtLeast3Times()
    {
        await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1);

        // At least 3 calls: start, intermediate, completion (FR-001, SC-001)
        var calls = _progressReporter.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(IProgressReporter.ReportAsync));
        Assert.True(calls >= 3, $"Expected at least 3 progress reports, got {calls}");
    }

    [Fact]
    public async Task ExecuteAsync_ProgressMessages_AreShortAndContainPosition()
    {
        var capturedMessages = new List<string>();
        _progressReporter.ReportAsync(
            Arg.Any<object?>(), Arg.Any<int>(), Arg.Any<int?>(),
            Arg.Do<string>(m => capturedMessages.Add(m)), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1);

        // SC-002: messages ≤80 chars
        Assert.All(capturedMessages, m =>
            Assert.True(m.Length <= 80, $"Message exceeds 80 chars: \"{m}\" ({m.Length} chars)"));

        // SC-002: at least one message contains a position indicator (N/M pattern)
        Assert.Contains(capturedMessages, m =>
            System.Text.RegularExpressions.Regex.IsMatch(m, @"\d+/\d+"));
    }

    // ─── Feature 013: Copilot review progress notifications ──────────────────

    [Fact]
    public async Task Execute_CopilotAvailable_SendsCopilotReviewStartedProgress()
    {
        _copilotAvailability.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(true);
        var copilotResult = new Core.Models.CopilotReview.CopilotReviewResult
        {
            ReviewKey = "pr:42",
            PageReviews = new[]
            {
                Core.Models.CopilotReview.CopilotPageReviewResult.Success(1, "ok", 1),
            },
            CompletedAt = DateTimeOffset.UtcNow,
        };
        _copilotReviewOrchestrator
            .WaitForReviewAsync("pr:42", Arg.Any<CancellationToken>())
            .Returns(copilotResult);

        var capturedMessages = new List<string>();
        _progressReporter.ReportAsync(
            Arg.Any<object?>(), Arg.Any<int>(), Arg.Any<int?>(),
            Arg.Do<string>(m => capturedMessages.Add(m)), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1);

        Assert.Contains(capturedMessages, m =>
            m.Contains("Copilot review started", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Execute_CopilotReviewInProgress_ReportsPageLevelProgress()
    {
        _copilotAvailability.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(true);

        var copilotResult = new Core.Models.CopilotReview.CopilotReviewResult
        {
            ReviewKey = "pr:42",
            PageReviews = new[]
            {
                Core.Models.CopilotReview.CopilotPageReviewResult.Success(1, "review page 1", 1),
                Core.Models.CopilotReview.CopilotPageReviewResult.Success(2, "review page 2", 1),
                Core.Models.CopilotReview.CopilotPageReviewResult.Success(3, "review page 3", 1),
            },
            CompletedAt = DateTimeOffset.UtcNow,
        };

        // WaitForReviewAsync completes after a short delay to allow the polling loop to run.
        var completionTcs = new TaskCompletionSource<Core.Models.CopilotReview.CopilotReviewResult>();
        _copilotReviewOrchestrator
            .WaitForReviewAsync("pr:42", Arg.Any<CancellationToken>())
            .Returns(completionTcs.Task);

        // Simulate progress snapshots: pages completing over time.
        var snapshotCallCount = 0;
        _copilotReviewOrchestrator.TryGetSnapshot("pr:42")
            .Returns(_ =>
            {
                snapshotCallCount++;
                return new Core.Models.CopilotReview.CopilotReviewSnapshot
                {
                    ReviewKey = "pr:42",
                    Status = Core.Models.CopilotReview.CopilotReviewStatus.Reviewing,
                    TotalPages = 3,
                    CompletedPages = Math.Min(snapshotCallCount, 3),
                };
            });

        var capturedMessages = new List<string>();
        _progressReporter.ReportAsync(
            Arg.Any<object?>(), Arg.Any<int>(), Arg.Any<int?>(),
            Arg.Do<string>(m => capturedMessages.Add(m)), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // Complete the review after a brief delay so the polling loop iterates.
        _ = Task.Run(async () =>
        {
            await Task.Delay(300);
            completionTcs.SetResult(copilotResult);
        });

        var blocks = (await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1)).ToList();

        // Verify page-level progress was reported (at least one "K/3 pages complete" message).
        Assert.Contains(capturedMessages, m =>
            m.Contains("pages complete", StringComparison.OrdinalIgnoreCase) &&
            m.Contains("/3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Execute_CopilotReview_ContentOnlyPath_NoReviewStartedProgress()
    {
        // Default: Copilot not available — no "review started" message should appear.
        var capturedMessages = new List<string>();
        _progressReporter.ReportAsync(
            Arg.Any<object?>(), Arg.Any<int>(), Arg.Any<int?>(),
            Arg.Do<string>(m => capturedMessages.Add(m)), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1);

        Assert.DoesNotContain(capturedMessages, m =>
            m.Contains("Copilot review started", StringComparison.OrdinalIgnoreCase));
    }

    // ─── Feature 018 US2 (T026) — strict-mode CopilotUnavailableException handling ───

    private static CopilotVerdict StrictModeVerdict() => new(
        IsAvailable: false,
        Reason: CopilotAuthReason.NotAuthenticated,
        TokenSource: CopilotTokenSource.LoggedInUser,
        ConfiguredModel: "claude-sonnet-4.6",
        EntitledModels: Array.Empty<string>(),
        Login: null,
        Host: null,
        Remediation: "Run 'gh auth login' with Copilot scopes.");

    [Fact]
    public async Task Execute_StrictModeCopilotUnavailable_SurfacesAsMcpErrorWithRemediation()
    {
        // T026(a): strict mode throws CopilotUnavailableException from IsAvailableAsync.
        // The handler lets it bubble; the MCP tool-handler infrastructure wraps it in an
        // McpException (the tool-error envelope) whose message + InnerException carry the
        // remediation string. Review MUST NOT be triggered.
        var verdict = StrictModeVerdict();
        _copilotAvailability.IsAvailableAsync(Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new CopilotUnavailableException(verdict));

        var ex = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(
            () => _handler.ExecuteAsync(prNumber: 42, pageNumber: 1));

        // The remediation must reach the operator — either inline or via the inner exception.
        Assert.True(
            ex.Message.Contains(verdict.Remediation, StringComparison.Ordinal)
                || ex.InnerException is CopilotUnavailableException { Verdict.Remediation: var r } && r == verdict.Remediation,
            $"Expected the McpException to carry the remediation string. Got: {ex.Message}");
        _copilotReviewOrchestrator.DidNotReceive().TriggerReview(Arg.Any<string>(), Arg.Any<IEnrichmentResult>());
    }

    [Fact]
    public async Task Execute_GracefulModeCopilotUnavailable_FallsBackToContentOnly()
    {
        // T026(b): regression guard — graceful mode path is untouched.
        // IsAvailableAsync returns false (no throw) and the handler falls back to
        // the content-only path as before.
        _copilotAvailability.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(false);

        var blocks = (await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1)).ToList();
        var text = AllText(blocks);

        Assert.Contains("src/A.cs", text);
        _copilotReviewOrchestrator.DidNotReceive().TriggerReview(Arg.Any<string>(), Arg.Any<IEnrichmentResult>());
    }

    [Fact]
    public async Task Execute_UnrelatedExceptionFromIsAvailableAsync_StillPropagates()
    {
        // T026(c): regression guard — non-CopilotUnavailableException exceptions from
        // IsAvailableAsync must still bubble out via the existing error-handling path.
        // The new catch block must NOT swallow them.
        _copilotAvailability.IsAvailableAsync(Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("unrelated failure"));

        await Assert.ThrowsAnyAsync<Exception>(
            () => _handler.ExecuteAsync(prNumber: 42, pageNumber: 1));
    }
}
