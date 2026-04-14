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
internal sealed class CopilotReviewOrchestrator : ICopilotReviewOrchestrator
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
            _ = Task.Run(() => BackgroundBodyAsync(job, enrichmentResult), CancellationToken.None);
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
                }
                job.Completion.TrySetResult(emptyResult);
                return;
            }

            // Dispatch all pages concurrently. CopilotRequestThrottle (3-second
            // SemaphoreSlim gate) serializes the outgoing SDK calls, but model response
            // wait times overlap — giving a real wall-time reduction of roughly
            // 3s × (N-1) + max(response_time) vs N × response_time sequentially.
            // Each task writes to its own pageResults[idx] slot (no contention).
            // CopilotReviewWaiter observes CompletedPages via polling — order-agnostic.
            var pageResults = new CopilotPageReviewResult[allocation.TotalPages];
            var pageTasks = new Task[allocation.TotalPages];
            for (var pageIdx = 0; pageIdx < allocation.TotalPages; pageIdx++)
            {
                var idx = pageIdx;
                var pageSlice = allocation.Pages[idx];
                var pageNumber = pageSlice.PageNumber;
                var (enrichedContent, filePaths) = BuildPageInput(pageSlice, enrichment);
                pageTasks[idx] = Task.Run(async () =>
                {
                    pageResults[idx] = await ReviewPageAndTrackAsync(
                        job, pageNumber, enrichedContent, filePaths, ct);
                }, ct);
            }
            await Task.WhenAll(pageTasks);

            var result = new CopilotReviewResult
            {
                ReviewKey = job.ReviewKey,
                PageReviews = pageResults,
                CompletedAt = DateTimeOffset.UtcNow,
            };

            lock (_lock)
            {
                job.Result = result;
                job.Status = CopilotReviewStatus.Ready;
            }

            _logger.LogInformation(
                Resources.LogCopilotReviewCompleted,
                job.ReviewKey, result.SucceededPages, result.TotalPages);

            job.Completion.TrySetResult(result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Copilot review for '{ReviewKey}' cancelled by shutdown", job.ReviewKey);
            lock (_lock) { job.Status = CopilotReviewStatus.Failed; job.ErrorMessage = "cancelled"; }
            job.Completion.TrySetCanceled(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, Resources.LogCopilotReviewFailed, job.ReviewKey, ex.Message);
            lock (_lock) { job.Status = CopilotReviewStatus.Failed; job.ErrorMessage = ex.Message; }
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

        // Feature 021: post-review finding validation. Runs only when the review
        // succeeded, the feature is enabled, and the validator+resolver are wired in DI.
        if (result.Succeeded
            && _options.Value.ValidateFindings
            && _findingValidator is not null
            && _findingScopeResolver is not null)
        {
            try
            {
                result = await ValidateAndFilterAsync(job, pageNumber, result, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Graceful degradation (FR-012): validation failure must not break the
                // review — pass the original result through unfiltered.
                _logger.LogWarning(ex,
                    "Validation pass failed for '{ReviewKey}' page {PageNumber}; returning original result",
                    job.ReviewKey, pageNumber);
            }
        }

        Interlocked.Increment(ref job.CompletedPages);
        return result;
    }

    /// <summary>
    /// Parses findings from the review text, resolves each finding's enclosing scope,
    /// validates them via a second Copilot pass, and rebuilds the result with
    /// false-positives removed and uncertain findings tagged. Feature 021.
    /// </summary>
    private async Task<CopilotPageReviewResult> ValidateAndFilterAsync(
        CopilotReviewJob job,
        int pageNumber,
        CopilotPageReviewResult original,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(original.ReviewText))
            return original;

        job.CurrentActivity = $"Page {pageNumber}/{job.TotalPages}: parsing findings for validation";
        var (findings, remainder) = FindingParser.Parse(original.ReviewText!);

        // Zero parseable findings → nothing to validate. Return the original review
        // text as-is (no summary footer; SC-005 scoping excludes this case).
        if (findings.Count == 0)
            return original;

        // Over-threshold → skip validation for this page (FR-015). Likely a systemic
        // issue; individual validation isn't the right tool.
        var maxValidatable = _options.Value.MaxValidatableFindings;
        if (maxValidatable > 0 && findings.Count > maxValidatable)
        {
            _logger.LogInformation(
                "Skipping validation for '{ReviewKey}' page {PageNumber}: {Count} findings exceed MaxValidatableFindings={Max}",
                job.ReviewKey, pageNumber, findings.Count, maxValidatable);
            return original;
        }

        job.CurrentActivity = $"Page {pageNumber}/{job.TotalPages}: resolving {findings.Count} scopes";
        var withScopes = await _findingScopeResolver!.ResolveAsync(
            findings, _options.Value.MaxScopeLines, ct).ConfigureAwait(false);

        job.CurrentActivity = $"Page {pageNumber}/{job.TotalPages}: validating {findings.Count} findings";
        var validated = await _findingValidator!.ValidateAsync(withScopes, ct).ConfigureAwait(false);

        var filteredText = FindingFilterer.Apply(remainder, validated);
        return CopilotPageReviewResult.Success(pageNumber, filteredText, attemptsMade: original.AttemptsMade);
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

            var attemptSuffix = attempt > 1 ? $" (attempt {attempt})" : "";
            job.CurrentActivity = $"Page {pageNumber}/{job.TotalPages}: creating session{attemptSuffix}";

            _logger.LogInformation(Resources.LogCopilotReviewPageStarted, job.ReviewKey, pageNumber, attempt);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            job.CurrentActivity = $"Page {pageNumber}/{job.TotalPages}: waiting for Copilot response{attemptSuffix}";

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

        /// <summary>Set under <c>_lock</c> once the allocation is computed.</summary>
        public int TotalPages { get; set; }

        /// <summary>Atomically incremented via <see cref="Interlocked.Increment"/> as each page finishes.</summary>
        public int CompletedPages;

        /// <summary>Short status message updated at key points for progress reporting.</summary>
        public volatile string? CurrentActivity;
    }
}
