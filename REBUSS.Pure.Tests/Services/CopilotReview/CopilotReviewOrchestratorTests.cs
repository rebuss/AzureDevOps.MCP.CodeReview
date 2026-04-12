using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Models.CopilotReview;
using REBUSS.Pure.Core.Models.Pagination;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Core.Services.CopilotReview;
using REBUSS.Pure.Services.CopilotReview;
using REBUSS.Pure.Services.PrEnrichment;

namespace REBUSS.Pure.Tests.Services.CopilotReview;

/// <summary>
/// Unit tests for <see cref="CopilotReviewOrchestrator"/> — feature 013 Phase 3 US1 (T028).
/// Retry-specific scenarios (partial failure, all-failed with file paths) arrive in T037 (US3).
/// </summary>
public class CopilotReviewOrchestratorTests
{
    private static CopilotReviewOrchestrator Create(
        ICopilotPageReviewer pageReviewer,
        IPageAllocator? pageAllocator = null,
        int budget = 128_000)
    {
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(CancellationToken.None);
        return new CopilotReviewOrchestrator(
            pageReviewer,
            pageAllocator ?? BuildAllocator(),
            Options.Create(new CopilotReviewOptions { ReviewBudgetTokens = budget }),
            lifetime,
            NullLogger<CopilotReviewOrchestrator>.Instance);
    }

    /// <summary>Default allocator: echoes its input into a single page.</summary>
    private static IPageAllocator BuildAllocator(int numberOfPages = 2)
    {
        var allocator = Substitute.For<IPageAllocator>();
        allocator.Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), Arg.Any<int>())
            .Returns(ci =>
            {
                var candidates = ci.Arg<IReadOnlyList<PackingCandidate>>();
                var pages = new List<PageSlice>();
                if (numberOfPages == 0)
                    return new PageAllocation(pages, 0, 0);
                var itemsPerPage = Math.Max(1, candidates.Count / numberOfPages);
                for (var p = 0; p < numberOfPages; p++)
                {
                    var start = p * itemsPerPage;
                    var end = p == numberOfPages - 1 ? candidates.Count : (p + 1) * itemsPerPage;
                    var items = Enumerable.Range(start, end - start)
                        .Select(i => new PageSliceItem(i, PackingItemStatus.Included, 500))
                        .ToArray();
                    pages.Add(new PageSlice(p + 1, start, end, items, 500 * items.Length, 100_000));
                }
                return new PageAllocation(pages, numberOfPages, candidates.Count);
            });
        return allocator;
    }

    private static PrEnrichmentResult BuildEnrichment(int fileCount = 4) =>
        new()
        {
            PrNumber = 42,
            HeadSha = "abc",
            SortedCandidates = Enumerable.Range(0, fileCount)
                .Select(i => new PackingCandidate($"src/F{i}.cs", 500, FileCategory.Source, 35))
                .ToArray(),
            EnrichedByPath = Enumerable.Range(0, fileCount)
                .ToDictionary(i => $"src/F{i}.cs", i => $"=== src/F{i}.cs ===\nbody{i}", StringComparer.OrdinalIgnoreCase),
            Allocation = new PageAllocation(Array.Empty<PageSlice>(), 0, 0),
            SafeBudgetTokens = 128_000,
            CompletedAt = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task TriggerReview_AllPagesSucceed_ResultContainsAllPages()
    {
        var reviewer = Substitute.For<ICopilotPageReviewer>();
        reviewer.ReviewPageAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(CopilotPageReviewResult.Success(ci.Arg<int>(), "ok", 1)));

        var orchestrator = Create(reviewer);
        orchestrator.TriggerReview(42, BuildEnrichment());
        var result = await orchestrator.WaitForReviewAsync(42, CancellationToken.None);

        Assert.Equal(42, result.PrNumber);
        Assert.Equal(2, result.TotalPages);
        Assert.Equal(2, result.SucceededPages);
        Assert.Equal(0, result.FailedPages);
        Assert.All(result.PageReviews, p => Assert.True(p.Succeeded));
    }

    [Fact]
    public async Task TriggerReview_Idempotent_SamePrDoesNotRetrigger()
    {
        var reviewer = Substitute.For<ICopilotPageReviewer>();
        reviewer.ReviewPageAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(CopilotPageReviewResult.Success(ci.Arg<int>(), "ok", 1)));

        var orchestrator = Create(reviewer);
        var enrichment = BuildEnrichment();
        orchestrator.TriggerReview(42, enrichment);
        orchestrator.TriggerReview(42, enrichment); // second trigger
        orchestrator.TriggerReview(42, enrichment); // third trigger
        _ = await orchestrator.WaitForReviewAsync(42, CancellationToken.None);

        // 2 pages × 1 trigger = 2 calls total (not 6).
        await reviewer.Received(2).ReviewPageAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryGetSnapshot_BeforeTrigger_ReturnsNull()
    {
        var reviewer = Substitute.For<ICopilotPageReviewer>();
        var orchestrator = Create(reviewer);
        Assert.Null(orchestrator.TryGetSnapshot(999));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task TryGetSnapshot_AfterCompletion_ReturnsReadyWithResult()
    {
        var reviewer = Substitute.For<ICopilotPageReviewer>();
        reviewer.ReviewPageAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(CopilotPageReviewResult.Success(ci.Arg<int>(), "ok", 1)));

        var orchestrator = Create(reviewer);
        orchestrator.TriggerReview(42, BuildEnrichment());
        _ = await orchestrator.WaitForReviewAsync(42, CancellationToken.None);

        var snapshot = orchestrator.TryGetSnapshot(42);
        Assert.NotNull(snapshot);
        Assert.Equal(CopilotReviewStatus.Ready, snapshot!.Status);
        Assert.NotNull(snapshot.Result);
        Assert.Equal(42, snapshot.Result!.PrNumber);
    }

    [Fact]
    public async Task TryGetSnapshot_AfterCompletion_ExposesTotalAndCompletedPages()
    {
        var reviewer = Substitute.For<ICopilotPageReviewer>();
        reviewer.ReviewPageAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(CopilotPageReviewResult.Success(ci.Arg<int>(), "ok", 1)));

        var orchestrator = Create(reviewer);
        orchestrator.TriggerReview(42, BuildEnrichment());
        _ = await orchestrator.WaitForReviewAsync(42, CancellationToken.None);

        var snapshot = orchestrator.TryGetSnapshot(42);
        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot!.TotalPages);
        Assert.Equal(2, snapshot.CompletedPages);
    }

    [Fact]
    public async Task TriggerReview_EmptyAllocation_ReturnsEmptyResult()
    {
        var reviewer = Substitute.For<ICopilotPageReviewer>();
        var orchestrator = Create(reviewer, pageAllocator: BuildAllocator(numberOfPages: 0));

        orchestrator.TriggerReview(42, BuildEnrichment());
        var result = await orchestrator.WaitForReviewAsync(42, CancellationToken.None);

        Assert.Equal(0, result.TotalPages);
        await reviewer.DidNotReceive().ReviewPageAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerReview_WrongEnrichmentType_ThrowsArgumentException()
    {
        var reviewer = Substitute.For<ICopilotPageReviewer>();
        var orchestrator = Create(reviewer);
        Assert.Throws<ArgumentException>(() => orchestrator.TriggerReview(42, "not an enrichment result"));
        await Task.CompletedTask;
    }

    // ─── Feature 013 Phase 5 US3 (T037) — 3-attempt retry scenarios ──────────────

    [Fact]
    public async Task ReviewPage_TransientFailureThenSuccess_RecoveredAfterRetry()
    {
        var reviewer = Substitute.For<ICopilotPageReviewer>();
        var callCount = 0;
        reviewer.ReviewPageAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                callCount++;
                if (callCount <= 2)
                    return Task.FromResult(CopilotPageReviewResult.Failure(
                        ci.Arg<int>(), Array.Empty<string>(), "transient", 1));
                return Task.FromResult(CopilotPageReviewResult.Success(ci.Arg<int>(), "recovered", 1));
            });

        // Single-page allocator so the failure count is predictable.
        var orchestrator = Create(reviewer, BuildAllocator(numberOfPages: 1));
        orchestrator.TriggerReview(42, BuildEnrichment(fileCount: 2));
        var result = await orchestrator.WaitForReviewAsync(42, CancellationToken.None);

        Assert.Equal(1, result.TotalPages);
        Assert.Equal(1, result.SucceededPages);
        Assert.Equal(0, result.FailedPages);
        Assert.Equal(3, result.PageReviews[0].AttemptsMade);
        Assert.Equal("recovered", result.PageReviews[0].ReviewText);
    }

    [Fact]
    public async Task ReviewPage_AllThreeAttemptsFail_ResultMarkedFailedWithFilePaths()
    {
        var reviewer = Substitute.For<ICopilotPageReviewer>();
        reviewer.ReviewPageAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(CopilotPageReviewResult.Failure(
                ci.Arg<int>(), Array.Empty<string>(), "persistent error", 1)));

        var orchestrator = Create(reviewer, BuildAllocator(numberOfPages: 1));
        orchestrator.TriggerReview(42, BuildEnrichment(fileCount: 3));
        var result = await orchestrator.WaitForReviewAsync(42, CancellationToken.None);

        Assert.Equal(1, result.TotalPages);
        Assert.Equal(0, result.SucceededPages);
        Assert.Equal(1, result.FailedPages);
        var failed = result.PageReviews[0];
        Assert.False(failed.Succeeded);
        Assert.Equal(3, failed.AttemptsMade);
        Assert.NotEmpty(failed.FailedFilePaths); // orchestrator filled in the paths
        Assert.Contains("persistent error", failed.ErrorMessage);

        // Reviewer invoked exactly 3 times (one page × 3 attempts).
        await reviewer.Received(3).ReviewPageAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Orchestrator_PartialFailure_ResultContainsBothSuccessAndFailureEntries()
    {
        // Page 1 always succeeds, page 2 always fails.
        var reviewer = Substitute.For<ICopilotPageReviewer>();
        reviewer.ReviewPageAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var pageNumber = ci.Arg<int>();
                return Task.FromResult(pageNumber == 1
                    ? CopilotPageReviewResult.Success(pageNumber, "page 1 ok", 1)
                    : CopilotPageReviewResult.Failure(pageNumber, Array.Empty<string>(), "page 2 broken", 1));
            });

        var orchestrator = Create(reviewer, BuildAllocator(numberOfPages: 2));
        orchestrator.TriggerReview(42, BuildEnrichment(fileCount: 4));
        var result = await orchestrator.WaitForReviewAsync(42, CancellationToken.None);

        Assert.Equal(2, result.TotalPages);
        Assert.Equal(1, result.SucceededPages);
        Assert.Equal(1, result.FailedPages);
        Assert.True(result.PageReviews[0].Succeeded);
        Assert.False(result.PageReviews[1].Succeeded);
        Assert.NotEmpty(result.PageReviews[1].FailedFilePaths);
    }

    [Fact]
    public async Task Orchestrator_AllPagesFail_ResultReturnsAllFailedNotException()
    {
        var reviewer = Substitute.For<ICopilotPageReviewer>();
        reviewer.ReviewPageAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(CopilotPageReviewResult.Failure(
                ci.Arg<int>(), Array.Empty<string>(), "down", 1)));

        var orchestrator = Create(reviewer, BuildAllocator(numberOfPages: 3));
        orchestrator.TriggerReview(42, BuildEnrichment(fileCount: 6));
        var result = await orchestrator.WaitForReviewAsync(42, CancellationToken.None);

        Assert.Equal(3, result.TotalPages);
        Assert.Equal(0, result.SucceededPages);
        Assert.Equal(3, result.FailedPages);
        Assert.All(result.PageReviews, p =>
        {
            Assert.False(p.Succeeded);
            Assert.NotEmpty(p.FailedFilePaths);
            Assert.Equal(3, p.AttemptsMade);
        });
    }
}
