using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Models.CopilotReview;
using REBUSS.Pure.Core.Services.CopilotReview;
using REBUSS.Pure.Properties;
using REBUSS.Pure.Services.CopilotReview.Validation;
using REBUSS.Pure.Services.PrEnrichment;

namespace REBUSS.Pure.Services.CopilotReview;

/// <summary>
/// Coordinates the server-side Copilot review of every page of enriched content.
/// Source-agnostic — serves both PR reviews and local self-reviews via string-based
/// review keys. Mirrors the <c>PrEnrichmentOrchestrator</c> trigger/wait/snapshot pattern.
/// </summary>
internal sealed class CopilotReviewOrchestrator : ICopilotReviewOrchestrator, IAsyncDisposable
{
    private readonly ICopilotPageReviewer _pageReviewer;
    private readonly IPageAllocator _pageAllocator;
    private readonly IOptions<CopilotReviewOptions> _options;
    private readonly ILogger<CopilotReviewOrchestrator> _logger;
    private readonly CancellationToken _shutdownToken;

    // Feature 021: optional finding validator. Null when ValidateFindings == false
    // (opt-out per spec US4). Also null if DI couldn't resolve it.
    private readonly FindingValidator? _findingValidator;
    private readonly FindingScopeResolver? _findingScopeResolver;

    private readonly ConcurrentDictionary<string, CopilotReviewJob> _jobs = new();
    private readonly object _lock = new();

    public CopilotReviewOrchestrator(
        ICopilotPageReviewer pageReviewer,
        IPageAllocator pageAllocator,
        IOptions<CopilotReviewOptions> options,
        IHostApplicationLifetime lifetime,
        ILogger<CopilotReviewOrchestrator> logger,
        FindingValidator? findingValidator = null,
        FindingScopeResolver? findingScopeResolver = null)
    {
        _pageReviewer = pageReviewer;
        _pageAllocator = pageAllocator;
        _options = options;
        _logger = logger;
        _shutdownToken = lifetime.ApplicationStopping;
        _findingValidator = findingValidator;
        _findingScopeResolver = findingScopeResolver;
    }

    public void TriggerReview(string reviewKey, IEnrichmentResult enrichmentResult)
    {
        ArgumentException.ThrowIfNullOrEmpty(reviewKey);
        ArgumentNullException.ThrowIfNull(enrichmentResult);

        lock (_lock)
        {
            // Opportunistic TTL sweep: reclaim memory from long-completed jobs before
            // deciding idempotency. Without this, `_jobs` grows unboundedly in a
            // long-running MCP server (one entry per reviewed PR forever, each holding
            // the full CopilotReviewResult text and background task references).
            SweepStaleJobs();

            // Idempotent — cache key is reviewKey only.
            // If a job already exists for this key, do nothing: the caller will observe
            // the same result via WaitForReviewAsync.
            if (_jobs.ContainsKey(reviewKey))
                return;

            var job = new CopilotReviewJob
            {
                ReviewKey = reviewKey,
                Completion = new TaskCompletionSource<CopilotReviewResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously),
            };
            _jobs[reviewKey] = job;

            // IMPORTANT: Task.Run with its own None token; the body honors _shutdownToken internally.
            // The handle is retained on the job so DisposeAsync can drain in-flight reviews on
            // host shutdown — otherwise the process may terminate before the body's
            // catch(OCE) branch updates Status/completes the TCS for pending waiters.
            job.BackgroundTask = Task.Run(() => BackgroundBodyAsync(job, enrichmentResult), CancellationToken.None);
        }
    }

    /// <summary>
    /// Evicts terminal jobs (Ready / Failed) whose <c>CompletedAt</c> is older than
    /// <see cref="CopilotReviewOptions.JobRetentionMinutes"/>. Called under <c>_lock</c>
    /// from <see cref="TriggerReview"/> — opportunistic, no timer thread needed.
    /// A retention value ≤ 0 disables the sweep.
    /// </summary>
    private void SweepStaleJobs()
    {
        var retentionMinutes = _options.Value.JobRetentionMinutes;
        if (retentionMinutes <= 0)
            return;

        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-retentionMinutes);
        foreach (var kvp in _jobs)
        {
            if (kvp.Value.CompletedAt is DateTimeOffset completedAt && completedAt < cutoff)
                _jobs.TryRemove(kvp.Key, out _);
        }
    }

    /// <summary>
    /// Registered by DI on host shutdown (singleton lifetime). The <c>_shutdownToken</c>
    /// (ApplicationStopping) has already fired by the time this runs, so each background
    /// body is either in its catch(OCE) branch or about to be. We await the in-flight tasks
    /// with a bounded timeout so shutdown cannot hang on a misbehaving body while still
    /// giving the graceful-cancellation path a chance to finish — update Status, publish
    /// ErrorMessage, and signal pending waiters through <see cref="CopilotReviewJob.Completion"/>.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        var tasks = _jobs.Values
            .Select(j => j.BackgroundTask)
            .Where(t => t is not null)
            .Cast<Task>()
            .ToArray();

        if (tasks.Length == 0)
            return;

        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch
        {
            // Individual task failures / cancellations are already surfaced to waiters via
            // their TaskCompletionSource. Do not propagate here — DisposeAsync must not throw.
        }
    }

    public Task<CopilotReviewResult> WaitForReviewAsync(string reviewKey, CancellationToken ct)
    {
        if (!_jobs.TryGetValue(reviewKey, out var job))
            throw new InvalidOperationException($"No Copilot review job for key '{reviewKey}'");

        // Caller's ct only governs the wait; the background body keeps running (FR-011).
        return job.Completion.Task.WaitAsync(ct);
    }

    public CopilotReviewSnapshot? TryGetSnapshot(string reviewKey)
    {
        if (!_jobs.TryGetValue(reviewKey, out var job))
            return null;

        lock (_lock)
        {
            return new CopilotReviewSnapshot
            {
                ReviewKey = job.ReviewKey,
                Status = job.Status,
                Result = job.Result,
                ErrorMessage = job.ErrorMessage,
                TotalPages = job.TotalPages,
                CompletedPages = Volatile.Read(ref job.CompletedPages),
                CurrentActivity = job.CurrentActivity,
            };
        }
    }

    private async Task BackgroundBodyAsync(CopilotReviewJob job, IEnrichmentResult enrichment)
    {
        var ct = _shutdownToken;
        try
        {
            // Re-paginate the enrichment result against the Copilot-specific budget.
            // (research.md Decision 7 — IDE gateway budget ≠ Copilot review budget.)
            var allocation = _pageAllocator.Allocate(
                enrichment.SortedCandidates, _options.Value.ReviewBudgetTokens);

            _logger.LogInformation(
                Resources.LogCopilotReviewTriggered, job.ReviewKey, allocation.TotalPages);

            lock (_lock) { job.TotalPages = allocation.TotalPages; }
            job.CurrentActivity = $"Allocated {allocation.TotalPages} pages — reviewing in parallel";

            // Empty-allocation fast path (edge case: empty PR / zero pages).
            if (allocation.TotalPages == 0)
            {
                var emptyResult = new CopilotReviewResult
                {
                    ReviewKey = job.ReviewKey,
                    PageReviews = Array.Empty<CopilotPageReviewResult>(),
                    CompletedAt = DateTimeOffset.UtcNow,
                };
                lock (_lock)
                {
                    job.Result = emptyResult;
                    job.Status = CopilotReviewStatus.Ready;
                    job.CompletedAt = DateTimeOffset.UtcNow;
                }
                job.Completion.TrySetResult(emptyResult);
                return;
            }

            // Dispatch pages in batches of MaxConcurrentPages. Within a batch,
            // CopilotRequestThrottle (SemaphoreSlim gate, spacing configured via
            // CopilotReviewOptions.MinRequestIntervalSeconds) serializes the outgoing
            // SDK calls; we then await every batch before starting the next one. This caps
            // the number of simultaneously in-flight Copilot requests — the GitHub backend
            // rate-limits larger fan-outs and silently re-queues the overflow, which
            // otherwise doubles wall-clock time. Each task writes to its own
            // pageResults[idx] slot (no contention). CopilotReviewWaiter observes
            // CompletedPages via polling — order-agnostic.
            var pageResults = new CopilotPageReviewResult[allocation.TotalPages];
            var batchSize = Math.Max(1, _options.Value.MaxConcurrentPages);
            for (var batchStart = 0; batchStart < allocation.TotalPages; batchStart += batchSize)
            {
                var batchEnd = Math.Min(batchStart + batchSize, allocation.TotalPages);
                var batchTasks = new Task[batchEnd - batchStart];
                for (var pageIdx = batchStart; pageIdx < batchEnd; pageIdx++)
                {
                    var idx = pageIdx;
                    var pageSlice = allocation.Pages[idx];
                    var pageNumber = pageSlice.PageNumber;
                    var (enrichedContent, filePaths) = BuildPageInput(pageSlice, enrichment);
                    batchTasks[idx - batchStart] = Task.Run(async () =>
                    {
                        pageResults[idx] = await ReviewPageAndTrackAsync(
                            job, pageNumber, enrichedContent, filePaths, ct);
                    }, ct);
                }
                await Task.WhenAll(batchTasks);
            }

            // Feature 021: consolidated finding validation across all pages.
            // Runs once after all pages complete (not per-page) to minimize Copilot calls.
            if (_options.Value.ValidateFindings
                && _findingValidator is not null
                && _findingScopeResolver is not null)
            {
                try
                {
                    await ValidateAllFindingsAsync(job, pageResults, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Graceful degradation (FR-012): validation failure must not break the
                    // review — pass the original results through unfiltered.
                    _logger.LogWarning(ex,
                        "Consolidated validation failed for '{ReviewKey}'; returning original results",
                        job.ReviewKey);
                }
            }

            var result = new CopilotReviewResult
            {
                ReviewKey = job.ReviewKey,
                PageReviews = pageResults,
                CompletedAt = DateTimeOffset.UtcNow,
            };

            // Publish a final progress activity — the waiter surfaces this as the last
            // IDE notification so the user sees "Review complete" rather than the review
            // flipping silent right after "N/N pages complete".
            job.CurrentActivity = $"Review complete — {result.SucceededPages}/{result.TotalPages} pages succeeded";

            lock (_lock)
            {
                job.Result = result;
                job.Status = CopilotReviewStatus.Ready;
                job.CompletedAt = DateTimeOffset.UtcNow;
            }

            _logger.LogInformation(
                Resources.LogCopilotReviewCompleted,
                job.ReviewKey, result.SucceededPages, result.TotalPages);

            job.Completion.TrySetResult(result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Copilot review for '{ReviewKey}' cancelled by shutdown", job.ReviewKey);
            lock (_lock)
            {
                job.Status = CopilotReviewStatus.Failed;
                job.ErrorMessage = "cancelled";
                job.CompletedAt = DateTimeOffset.UtcNow;
            }
            job.Completion.TrySetCanceled(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, Resources.LogCopilotReviewFailed, job.ReviewKey, ex.Message);
            lock (_lock)
            {
                job.Status = CopilotReviewStatus.Failed;
                job.ErrorMessage = ex.Message;
                job.CompletedAt = DateTimeOffset.UtcNow;
            }
            job.Completion.TrySetException(ex);
        }
    }

    private const int MaxAttemptsPerPage = 3;

    /// <summary>
    /// Wraps <see cref="ReviewPageWithRetryAsync"/> and atomically increments the job's
    /// completed-page counter when the page finishes (whether it succeeded or failed after
    /// exhausting retries). Cancellation (<see cref="OperationCanceledException"/>) is
    /// re-thrown without incrementing — the orchestrator-level catch handles that state.
    /// </summary>
    private async Task<CopilotPageReviewResult> ReviewPageAndTrackAsync(
        CopilotReviewJob job,
        int pageNumber,
        string enrichedContent,
        IReadOnlyList<string> filePathsOnPage,
        CancellationToken ct)
    {
        var result = await ReviewPageWithRetryAsync(job, pageNumber, enrichedContent, filePathsOnPage, ct);
        Interlocked.Increment(ref job.CompletedPages);
        return result;
    }

    /// <summary>
    /// Consolidated post-review validation across all successfully reviewed pages.
    /// Parses findings from every succeeded page, resolves enclosing scopes, validates
    /// them via a single Copilot pass (internally batched by <see cref="FindingValidator"/>),
    /// and rebuilds each page's <see cref="CopilotPageReviewResult.ReviewText"/> with
    /// false-positives removed and uncertain findings tagged. Feature 021.
    /// </summary>
    private async Task ValidateAllFindingsAsync(
        CopilotReviewJob job,
        CopilotPageReviewResult[] pageResults,
        CancellationToken ct)
    {
        // Phase 1: parse findings from all successful pages, tracking page origin.
        var pageData = new List<(int PageIndex, IReadOnlyList<ParsedFinding> Findings, string Remainder)>();
        var allFindings = new List<ParsedFinding>();

        for (var i = 0; i < pageResults.Length; i++)
        {
            var page = pageResults[i];
            if (!page.Succeeded || string.IsNullOrWhiteSpace(page.ReviewText))
                continue;

            var (findings, remainder) = FindingParser.Parse(page.ReviewText!);
            if (findings.Count == 0)
                continue;

            pageData.Add((i, findings, remainder));
            allFindings.AddRange(findings);
        }

        if (allFindings.Count == 0)
            return;

        // Over-threshold → skip validation entirely (FR-015). Likely a systemic issue;
        // individual finding validation isn't the right tool. Logged at Warning because
        // this is graceful degradation with user-visible impact — the review bypasses
        // false-positive filtering for the entire PR — and operators should see it.
        var maxValidatable = _options.Value.MaxValidatableFindings;
        if (maxValidatable > 0 && allFindings.Count > maxValidatable)
        {
            _logger.LogWarning(
                "Skipping validation for '{ReviewKey}': {Count} total findings exceed MaxValidatableFindings={Max}",
                job.ReviewKey, allFindings.Count, maxValidatable);
            return;
        }

        // Phase 2: resolve enclosing scopes for all findings at once.
        job.CurrentActivity = $"Resolving {allFindings.Count} scopes for validation";
        var withScopes = await _findingScopeResolver!.ResolveAsync(
            allFindings, _options.Value.MaxScopeLines, ct).ConfigureAwait(false);

        // Phase 3: validate all findings (internal pagination by token budget).
        // The pageProgress callback surfaces intra-validation progress so the IDE
        // notification advances per validation page rather than sitting on a single
        // message.
        job.CurrentActivity = $"Validating {allFindings.Count} findings";
        var validated = await _findingValidator!.ValidateAsync(
            withScopes, job.ReviewKey, ct,
            pageProgress: (pageNumber, totalPages) =>
            {
                job.CurrentActivity = totalPages > 1
                    ? $"Validating findings: page {pageNumber}/{totalPages}"
                    : $"Validating {allFindings.Count} findings";
            }).ConfigureAwait(false);

        LogValidationSummary(job.ReviewKey, withScopes, validated);

        // Phase 4: map validated findings back to their originating pages and rebuild ReviewText.
        var offset = 0;
        foreach (var (pageIndex, findings, remainder) in pageData)
        {
            var pageValidated = new List<ValidatedFinding>(findings.Count);
            for (var j = 0; j < findings.Count; j++)
                pageValidated.Add(validated[offset + j]);
            offset += findings.Count;

            var filteredText = FindingFilterer.Apply(remainder, pageValidated);
            pageResults[pageIndex] = CopilotPageReviewResult.Success(
                pageResults[pageIndex].PageNumber, filteredText,
                attemptsMade: pageResults[pageIndex].AttemptsMade);
        }
    }

    /// <summary>
    /// Emits a single Information-level summary of the validation pipeline so silent
    /// drops (e.g. archive at the wrong commit producing SourceUnavailable for every
    /// added file) are visible in the log instead of requiring inspection-file archaeology.
    /// </summary>
    private void LogValidationSummary(
        string reviewKey,
        IReadOnlyList<Validation.FindingWithScope> withScopes,
        IReadOnlyList<Validation.ValidatedFinding> validated)
    {
        int notCSharp = 0, sourceUnavailable = 0, scopeNotFound = 0, resolved = 0;
        foreach (var w in withScopes)
        {
            switch (w.ResolutionFailure)
            {
                case Validation.ScopeResolutionFailure.NotCSharp:        notCSharp++; break;
                case Validation.ScopeResolutionFailure.SourceUnavailable: sourceUnavailable++; break;
                case Validation.ScopeResolutionFailure.ScopeNotFound:     scopeNotFound++; break;
                case Validation.ScopeResolutionFailure.None:              resolved++; break;
            }
        }

        int valid = 0, falsePositive = 0, uncertain = 0;
        foreach (var v in validated)
        {
            switch (v.Verdict)
            {
                case Validation.FindingVerdict.Valid:         valid++; break;
                case Validation.FindingVerdict.FalsePositive: falsePositive++; break;
                case Validation.FindingVerdict.Uncertain:     uncertain++; break;
            }
        }

        _logger.LogInformation(
            "Finding validation summary for '{ReviewKey}': parsed={Parsed} " +
            "(scope: not-csharp={NotCs}, source-unavailable={SrcUnavail}, scope-not-found={ScopeMiss}, resolved={Resolved}) " +
            "→ verdicts: valid={Valid}, false-positive={FalsePositive}, uncertain={Uncertain}",
            reviewKey, validated.Count,
            notCSharp, sourceUnavailable, scopeNotFound, resolved,
            valid, falsePositive, uncertain);
    }

    /// <summary>
    /// Wraps <see cref="ICopilotPageReviewer.ReviewPageAsync"/> in a bounded 3-attempt retry
    /// loop per Clarification Q1 / research.md Decision 3. No backoff — retries fire
    /// immediately. On exhaustion, returns a failure result with the file paths that were
    /// on this page so the IDE agent can surface them for manual follow-up.
    /// </summary>
    private async Task<CopilotPageReviewResult> ReviewPageWithRetryAsync(
        CopilotReviewJob job,
        int pageNumber,
        string enrichedContent,
        IReadOnlyList<string> filePathsOnPage,
        CancellationToken ct)
    {
        string lastError = "no attempts made";
        for (var attempt = 1; attempt <= MaxAttemptsPerPage; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            _logger.LogInformation(Resources.LogCopilotReviewPageStarted, job.ReviewKey, pageNumber, attempt);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Per-page activity is intentionally not published: pages run in parallel and
            // each concurrent task would overwrite CurrentActivity with its own "waiting"
            // message, making the progress feed flip-flop between pages. The monotonic
            // CompletedPages counter (emitted as "X/Y pages complete" by the waiter) is the
            // sole page-level progress signal. Per-attempt retries are visible in the log.
            CopilotPageReviewResult result;
            try
            {
                result = await _pageReviewer.ReviewPageAsync(job.ReviewKey, pageNumber, enrichedContent, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Contract says the reviewer never throws; defence-in-depth: treat as failed attempt.
                result = CopilotPageReviewResult.Failure(
                    pageNumber, Array.Empty<string>(), ex.Message, attemptsMade: attempt);
            }

            sw.Stop();

            if (result.Succeeded && !string.IsNullOrWhiteSpace(result.ReviewText))
            {
                _logger.LogInformation(
                    Resources.LogCopilotReviewPageCompleted,
                    job.ReviewKey, pageNumber, attempt, sw.ElapsedMilliseconds);
                // Re-wrap so AttemptsMade reflects the retry that succeeded.
                return CopilotPageReviewResult.Success(pageNumber, result.ReviewText!, attempt);
            }

            lastError = result.ErrorMessage ?? "empty response";
            _logger.LogInformation(
                Resources.LogCopilotReviewPageFailed,
                job.ReviewKey, pageNumber, attempt, lastError);
        }

        // All 3 attempts exhausted — fill in the file paths (the orchestrator is the only
        // component that knows which files were on this page) and return the failure.
        return CopilotPageReviewResult.Failure(
            pageNumber, filePathsOnPage, lastError, attemptsMade: MaxAttemptsPerPage);
    }

    private static (string EnrichedContent, IReadOnlyList<string> FilePaths) BuildPageInput(
        Core.Models.Pagination.PageSlice pageSlice, IEnrichmentResult enrichment)
    {
        var sb = new StringBuilder();
        var paths = new List<string>(pageSlice.Items.Count);
        foreach (var item in pageSlice.Items)
        {
            var path = enrichment.SortedCandidates[item.OriginalIndex].Path;
            paths.Add(path);
            if (enrichment.EnrichedByPath.TryGetValue(path, out var enrichedText))
            {
                sb.Append(enrichedText);
                sb.AppendLine();
            }
        }
        return (sb.ToString(), paths);
    }

    /// <summary>
    /// Internal per-PR job state — mutable but guarded by the surrounding lock for writes
    /// and the <see cref="ConcurrentDictionary{TKey, TValue}"/> for concurrent add/lookup.
    /// Narrow exception to Principle VI (see plan.md Constitution Check VI).
    /// </summary>
    private sealed class CopilotReviewJob
    {
        public required string ReviewKey { get; init; }
        public CopilotReviewStatus Status { get; set; } = CopilotReviewStatus.Reviewing;
        public CopilotReviewResult? Result { get; set; }
        public string? ErrorMessage { get; set; }
        public required TaskCompletionSource<CopilotReviewResult> Completion { get; init; }

        /// <summary>
        /// Handle for the fire-and-forget background body. Set once inside the creation
        /// lock; awaited on shutdown (<see cref="CopilotReviewOrchestrator.DisposeAsync"/>)
        /// so the process does not terminate before graceful-cancellation finishes.
        /// </summary>
        public Task? BackgroundTask { get; set; }

        /// <summary>Set under <c>_lock</c> once the allocation is computed.</summary>
        public int TotalPages { get; set; }

        /// <summary>Atomically incremented via <see cref="Interlocked.Increment"/> as each page finishes.</summary>
        public int CompletedPages;

        /// <summary>Short status message updated at key points for progress reporting.</summary>
        public volatile string? CurrentActivity;

        /// <summary>
        /// Timestamp of when the job reached a terminal state (Ready / Failed). Used by
        /// the TTL sweep in <see cref="CopilotReviewOrchestrator.TriggerReview"/> to evict
        /// stale entries. <c>null</c> while the job is still in progress.
        /// </summary>
        public DateTimeOffset? CompletedAt { get; set; }
    }
}
