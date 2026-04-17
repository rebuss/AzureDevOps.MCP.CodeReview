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
        // Note: FindingValidator and FindingScopeResolver are left at null — tests
        // operate in opt-out mode (feature 021 US4). The orchestrator must produce
        // unchanged review text when either dependency is null.
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
        reviewer.ReviewPageAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(CopilotPageReviewResult.Success(ci.Arg<int>(), "ok", 1)));

        var orchestrator = Create(reviewer);
        orchestrator.TriggerReview("pr:42", BuildEnrichment());
        var result = await orchestrator.WaitForReviewAsync("pr:42", CancellationToken.None);

        Assert.Equal("pr:42", result.ReviewKey);
        Assert.Equal(2, result.TotalPages);
        Assert.Equal(2, result.SucceededPages);
        Assert.Equal(0, result.FailedPages);
        Assert.All(result.PageReviews, p => Assert.True(p.Succeeded));
    }

    [Fact]
    public async Task TriggerReview_Idempotent_SamePrDoesNotRetrigger()
    {
        var reviewer = Substitute.For<ICopilotPageReviewer>();
        reviewer.ReviewPageAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(CopilotPageReviewResult.Success(ci.Arg<int>(), "ok", 1)));

        var orchestrator = Create(reviewer);
        var enrichment = BuildEnrichment();
        orchestrator.TriggerReview("pr:42", enrichment);
        orchestrator.TriggerReview("pr:42", enrichment); // second trigger
        orchestrator.TriggerReview("pr:42", enrichment); // third trigger
        _ = await orchestrator.WaitForReviewAsync("pr:42", CancellationToken.None);

        // 2 pages × 1 trigger = 2 calls total (not 6).
        await reviewer.Received(2).ReviewPageAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryGetSnapshot_BeforeTrigger_ReturnsNull()
    {
        var reviewer = Substitute.For<ICopilotPageReviewer>();
        var orchestrator = Create(reviewer);
        Assert.Null(orchestrator.TryGetSnapshot("pr:999"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task TryGetSnapshot_AfterCompletion_ReturnsReadyWithResult()
    {
        var reviewer = Substitute.For<ICopilotPageReviewer>();
        reviewer.ReviewPageAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(CopilotPageReviewResult.Success(ci.Arg<int>(), "ok", 1)));

        var orchestrator = Create(reviewer);
        orchestrator.TriggerReview("pr:42", BuildEnrichment());
        _ = await orchestrator.WaitForReviewAsync("pr:42", CancellationToken.None);

        var snapshot = orchestrator.TryGetSnapshot("pr:42");
        Assert.NotNull(snapshot);
        Assert.Equal(CopilotReviewStatus.Ready, snapshot!.Status);
        Assert.NotNull(snapshot.Result);
        Assert.Equal("pr:42", snapshot.Result!.ReviewKey);
    }

    [Fact]
    public async Task TryGetSnapshot_AfterCompletion_ExposesTotalAndCompletedPages()
    {
        var reviewer = Substitute.For<ICopilotPageReviewer>();
        reviewer.ReviewPageAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(CopilotPageReviewResult.Success(ci.Arg<int>(), "ok", 1)));

        var orchestrator = Create(reviewer);
        orchestrator.TriggerReview("pr:42", BuildEnrichment());
        _ = await orchestrator.WaitForReviewAsync("pr:42", CancellationToken.None);

        var snapshot = orchestrator.TryGetSnapshot("pr:42");
        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot!.TotalPages);
        Assert.Equal(2, snapshot.CompletedPages);
    }

    [Fact]
    public async Task TriggerReview_AfterRetentionExpired_EvictsOldJobAndRunsAgain()
    {
        // Regression: jobs must be pruned from `_jobs` after their TTL expires, otherwise
        // the dictionary grows unboundedly in long-running MCP servers. The TTL is
        // configured via CopilotReviewOptions.JobRetentionMinutes.
        var reviewer = Substitute.For<ICopilotPageReviewer>();
        reviewer.ReviewPageAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(CopilotPageReviewResult.Success(ci.Arg<int>(), "ok", 1)));

        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(CancellationToken.None);
        var orchestrator = new CopilotReviewOrchestrator(
            reviewer,
            BuildAllocator(),
            // Very short retention — test triggers, waits, then re-triggers and expects the
            // old job to be swept (new SDK calls on the second trigger).
            Options.Create(new CopilotReviewOptions
            {
                ReviewBudgetTokens = 128_000,
                JobRetentionMinutes = 1,
            }),
            lifetime,
            NullLogger<CopilotReviewOrchestrator>.Instance);

        orchestrator.TriggerReview("pr:42", BuildEnrichment());
        _ = await orchestrator.WaitForReviewAsync("pr:42", CancellationToken.None);

        // Rewind CompletedAt to force the sweep to consider the job stale.
        var jobsField = typeof(CopilotReviewOrchestrator)
            .GetField("_jobs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var jobs = (System.Collections.IDictionary)jobsField.GetValue(orchestrator)!;
        var job = jobs["pr:42"]!;
        var completedAtProp = job.GetType().GetProperty("CompletedAt")!;
        completedAtProp.SetValue(job, DateTimeOffset.UtcNow.AddMinutes(-10));

        orchestrator.TriggerReview("pr:42", BuildEnrichment()); // should trigger sweep + new job
        _ = await orchestrator.WaitForReviewAsync("pr:42", CancellationToken.None);

        // 2 pages per trigger × 2 triggers = 4 SDK calls (sweep evicted the old job, so
        // the second TriggerReview was NOT idempotent-skipped).
        await reviewer.Received(4).ReviewPageAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerReview_RetentionDisabled_NeverEvicts()
    {
        // JobRetentionMinutes = 0 disables the sweep (opt-in retention for tests /
        // short-lived processes). Even with an "old" CompletedAt, the job survives.
        var reviewer = Substitute.For<ICopilotPageReviewer>();
        reviewer.ReviewPageAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(CopilotPageReviewResult.Success(ci.Arg<int>(), "ok", 1)));

        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(CancellationToken.None);
        var orchestrator = new CopilotReviewOrchestrator(
            reviewer,
            BuildAllocator(),
            Options.Create(new CopilotReviewOptions
            {
                ReviewBudgetTokens = 128_000,
                JobRetentionMinutes = 0, // disabled
            }),
            lifetime,
            NullLogger<CopilotReviewOrchestrator>.Instance);

        orchestrator.TriggerReview("pr:42", BuildEnrichment());
        _ = await orchestrator.WaitForReviewAsync("pr:42", CancellationToken.None);

        // Rewind CompletedAt — should still NOT evict because retention is disabled.
        var jobsField = typeof(CopilotReviewOrchestrator)
            .GetField("_jobs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var jobs = (System.Collections.IDictionary)jobsField.GetValue(orchestrator)!;
        var job = jobs["pr:42"]!;
        var completedAtProp = job.GetType().GetProperty("CompletedAt")!;
        completedAtProp.SetValue(job, DateTimeOffset.UtcNow.AddDays(-30));

        orchestrator.TriggerReview("pr:42", BuildEnrichment()); // idempotent — nothing happens
        _ = await orchestrator.WaitForReviewAsync("pr:42", CancellationToken.None);

        // Only one set of SDK calls (2 pages) — second trigger was a no-op.
        await reviewer.Received(2).ReviewPageAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisposeAsync_NoJobsInFlight_CompletesImmediately()
    {
        // Empty job dictionary path — nothing to await, should return instantly.
        var reviewer = Substitute.For<ICopilotPageReviewer>();
        var orchestrator = Create(reviewer);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await orchestrator.DisposeAsync();
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 500,
            $"DisposeAsync with no jobs should return quickly, took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task DisposeAsync_CompletedJob_AwaitsTaskHandleAndReturns()
    {
        // Completed job — BackgroundTask is already finished, DisposeAsync awaits a
        // completed Task and returns.
        var reviewer = Substitute.For<ICopilotPageReviewer>();
        reviewer.ReviewPageAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(CopilotPageReviewResult.Success(ci.Arg<int>(), "ok", 1)));

        var orchestrator = Create(reviewer);
        orchestrator.TriggerReview("pr:42", BuildEnrichment());
        _ = await orchestrator.WaitForReviewAsync("pr:42", CancellationToken.None);

        await orchestrator.DisposeAsync(); // should not hang / throw
    }

    [Fact]
    public async Task DisposeAsync_InFlightJobCancelledByShutdown_DrainsWithinTimeout()
    {
        // Regression: before adding IAsyncDisposable + BackgroundTask handle, the host
        // could terminate before the body's catch(OCE) branch ran, dropping in-flight
        // reviews silently. Now shutdown awaits the drain (capped) so graceful cancellation
        // actually finishes.
        var shutdownCts = new CancellationTokenSource();
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(shutdownCts.Token);

        var gate = new TaskCompletionSource<CopilotPageReviewResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reviewer = Substitute.For<ICopilotPageReviewer>();
        reviewer.ReviewPageAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                // Honor cancellation: unblock with OCE when shutdownCts fires.
                var token = ci.Arg<CancellationToken>();
                return Task.Run(async () =>
                {
                    using var reg = token.Register(() => gate.TrySetCanceled(token));
                    return await gate.Task;
                });
            });

        var orchestrator = new CopilotReviewOrchestrator(
            reviewer,
            BuildAllocator(),
            Options.Create(new CopilotReviewOptions { ReviewBudgetTokens = 128_000 }),
            lifetime,
            NullLogger<CopilotReviewOrchestrator>.Instance);

        orchestrator.TriggerReview("pr:42", BuildEnrichment());

        // Simulate host shutdown: ApplicationStopping fires → body observes cancellation.
        shutdownCts.Cancel();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await orchestrator.DisposeAsync();
        sw.Stop();

        // Drain completes well within the 5s cap because cancellation propagates immediately.
        Assert.True(sw.ElapsedMilliseconds < 4000,
            $"DisposeAsync should drain promptly after shutdown, took {sw.ElapsedMilliseconds}ms");

        // Pending waiter sees cancellation surfaced (not a hang).
        var waitTask = orchestrator.WaitForReviewAsync("pr:42", CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitTask);
    }

    [Fact]
    public async Task TriggerReview_EmptyAllocation_ReturnsEmptyResult()
    {
        var reviewer = Substitute.For<ICopilotPageReviewer>();
        var orchestrator = Create(reviewer, pageAllocator: BuildAllocator(numberOfPages: 0));

        orchestrator.TriggerReview("pr:42", BuildEnrichment());
        var result = await orchestrator.WaitForReviewAsync("pr:42", CancellationToken.None);

        Assert.Equal(0, result.TotalPages);
        await reviewer.DidNotReceive().ReviewPageAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ─── Feature 013 Phase 5 US3 (T037) — 3-attempt retry scenarios ──────────────

    [Fact]
    public async Task ReviewPage_TransientFailureThenSuccess_RecoveredAfterRetry()
    {
        var reviewer = Substitute.For<ICopilotPageReviewer>();
        var callCount = 0;
        reviewer.ReviewPageAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
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
        orchestrator.TriggerReview("pr:42", BuildEnrichment(fileCount: 2));
        var result = await orchestrator.WaitForReviewAsync("pr:42", CancellationToken.None);

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
        reviewer.ReviewPageAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(CopilotPageReviewResult.Failure(
                ci.Arg<int>(), Array.Empty<string>(), "persistent error", 1)));

        var orchestrator = Create(reviewer, BuildAllocator(numberOfPages: 1));
        orchestrator.TriggerReview("pr:42", BuildEnrichment(fileCount: 3));
        var result = await orchestrator.WaitForReviewAsync("pr:42", CancellationToken.None);

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
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Orchestrator_PartialFailure_ResultContainsBothSuccessAndFailureEntries()
    {
        // Page 1 always succeeds, page 2 always fails.
        var reviewer = Substitute.For<ICopilotPageReviewer>();
        reviewer.ReviewPageAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var pageNumber = ci.Arg<int>();
                return Task.FromResult(pageNumber == 1
                    ? CopilotPageReviewResult.Success(pageNumber, "page 1 ok", 1)
                    : CopilotPageReviewResult.Failure(pageNumber, Array.Empty<string>(), "page 2 broken", 1));
            });

        var orchestrator = Create(reviewer, BuildAllocator(numberOfPages: 2));
        orchestrator.TriggerReview("pr:42", BuildEnrichment(fileCount: 4));
        var result = await orchestrator.WaitForReviewAsync("pr:42", CancellationToken.None);

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
        reviewer.ReviewPageAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(CopilotPageReviewResult.Failure(
                ci.Arg<int>(), Array.Empty<string>(), "down", 1)));

        var orchestrator = Create(reviewer, BuildAllocator(numberOfPages: 3));
        orchestrator.TriggerReview("pr:42", BuildEnrichment(fileCount: 6));
        var result = await orchestrator.WaitForReviewAsync("pr:42", CancellationToken.None);

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

    [Fact]
    public async Task TriggerReview_PrAndLocalKeys_ProduceIndependentJobs()
    {
        // Arrange: reviewer that always succeeds.
        var reviewer = Substitute.For<ICopilotPageReviewer>();
        reviewer.ReviewPageAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => CopilotPageReviewResult.Success(ci.Arg<int>(), "ok", 1));

        var orchestrator = Create(reviewer);
        var prEnrichment = BuildEnrichment(fileCount: 2);

        // Act: trigger two reviews with different key prefixes.
        orchestrator.TriggerReview("pr:42", prEnrichment);
        orchestrator.TriggerReview("local:staged:/repo", prEnrichment);

        var prResult = await orchestrator.WaitForReviewAsync("pr:42", CancellationToken.None);
        var localResult = await orchestrator.WaitForReviewAsync("local:staged:/repo", CancellationToken.None);

        // Assert: both complete independently with their own keys.
        Assert.Equal("pr:42", prResult.ReviewKey);
        Assert.Equal("local:staged:/repo", localResult.ReviewKey);

        var prSnapshot = orchestrator.TryGetSnapshot("pr:42");
        var localSnapshot = orchestrator.TryGetSnapshot("local:staged:/repo");
        Assert.NotNull(prSnapshot);
        Assert.NotNull(localSnapshot);
        Assert.Equal(CopilotReviewStatus.Ready, prSnapshot!.Status);
        Assert.Equal(CopilotReviewStatus.Ready, localSnapshot!.Status);

        // Snapshots are truly independent — querying a non-existent key returns null.
        Assert.Null(orchestrator.TryGetSnapshot("pr:999"));
    }

    [Fact]
    public async Task TriggerReview_MultiplePages_ExecutesConcurrently()
    {
        // Structural parallelism assertion — observes peak concurrent-call count,
        // NOT wall-clock duration. Timing-based variants ("parallel < sequentialMs")
        // flake on loaded CI where scheduling jitter or a single slow Task.Delay can
        // push the total past the threshold even though dispatch was parallel.
        const int pageCount = 3;

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allPagesInFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var countLock = new object();
        var concurrentCalls = 0;
        var peakConcurrency = 0;

        var reviewer = Substitute.For<ICopilotPageReviewer>();
        reviewer.ReviewPageAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                lock (countLock)
                {
                    concurrentCalls++;
                    if (concurrentCalls > peakConcurrency)
                        peakConcurrency = concurrentCalls;
                    if (concurrentCalls == pageCount)
                        allPagesInFlight.TrySetResult();
                }

                // Block every call here until the test releases the gate — guarantees
                // that only genuinely concurrent dispatch can reach the pageCount peak.
                await gate.Task;

                lock (countLock) { concurrentCalls--; }
                return CopilotPageReviewResult.Success(ci.Arg<int>(), "ok", 1);
            });

        var orchestrator = Create(reviewer, BuildAllocator(numberOfPages: pageCount));
        orchestrator.TriggerReview("pr:42", BuildEnrichment(fileCount: 6));

        // THE assertion — with a generous timeout, this either fires because all pages
        // reached the reviewer simultaneously or times out because dispatch was serial.
        await allPagesInFlight.Task.WaitAsync(TimeSpan.FromSeconds(5));

        gate.SetResult();
        var result = await orchestrator.WaitForReviewAsync("pr:42", CancellationToken.None);

        Assert.Equal(pageCount, result.TotalPages);
        Assert.Equal(pageCount, result.SucceededPages);
        Assert.Equal(pageCount, peakConcurrency);
    }

    [Fact]
    public async Task TriggerReview_PropagatesReviewKeyToPageReviewer()
    {
        // Feature 022: orchestrator must pass the review key as the first argument to
        // ICopilotPageReviewer.ReviewPageAsync so the inspection writer can group output
        // under a per-PR subdirectory.
        var reviewer = Substitute.For<ICopilotPageReviewer>();
        reviewer.ReviewPageAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(CopilotPageReviewResult.Success(ci.Arg<int>(), "ok", 1)));

        var orchestrator = Create(reviewer);
        orchestrator.TriggerReview("pr:42", BuildEnrichment());
        _ = await orchestrator.WaitForReviewAsync("pr:42", CancellationToken.None);

        await reviewer.Received().ReviewPageAsync(
            "pr:42", Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ─── Feature 021 — Finding validation integration (T028, T040) ────────────────
  
    [Fact]
    public async Task TriggerReview_NullValidator_ReviewTextUnchanged()
    {
        // When FindingValidator is null (opt-out path — spec US4), the orchestrator
        // must return the original review text without appending a validation footer.
        var reviewer = Substitute.For<ICopilotPageReviewer>();
        reviewer.ReviewPageAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(CopilotPageReviewResult.Success(
                ci.Arg<int>(),
                "**[critical]** `src/A.cs` (line 5): untouched finding",
                1)));

        // Create() passes null for validator/resolver.
        var orchestrator = Create(reviewer, BuildAllocator(numberOfPages: 1));
        orchestrator.TriggerReview("pr:42", BuildEnrichment(fileCount: 2));
        var result = await orchestrator.WaitForReviewAsync("pr:42", CancellationToken.None);

        var reviewText = result.PageReviews[0].ReviewText;
        Assert.Contains("untouched finding", reviewText);
        // Feature 021: no validation footer when validator is null.
        Assert.DoesNotContain("_Validation:", reviewText);
    }
}
