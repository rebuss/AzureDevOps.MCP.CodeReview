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

    private readonly ConcurrentDictionary<string, CopilotReviewJob> _jobs = new();
    private readonly object _lock = new();

    public CopilotReviewOrchestrator(
        ICopilotPageReviewer pageReviewer,
        IPageAllocator pageAllocator,
        IOptions<CopilotReviewOptions> options,
        IHostApplicationLifetime lifetime,
        ILogger<CopilotReviewOrchestrator> logger)
    {
        _pageReviewer = pageReviewer;
        _pageAllocator = pageAllocator;
        _options = options;
        _logger = logger;
        _shutdownToken = lifetime.ApplicationStopping;
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
            job.CurrentActivity = $"Allocated {allocation.TotalPages} pages — starting reviews";

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

            // Process pages sequentially so that progress notifications advance in order
            // (Page 1/N → 2/N → …). With the global request throttle serialising SDK calls,
            // parallel dispatch only caused status-message races without a real throughput gain.
            var pageResults = new CopilotPageReviewResult[allocation.TotalPages];
            for (var pageIdx = 0; pageIdx < allocation.TotalPages; pageIdx++)
            {
                var pageSlice = allocation.Pages[pageIdx];
                var pageNumber = pageSlice.PageNumber;
                var (enrichedContent, filePaths) = BuildPageInput(pageSlice, enrichment);
                pageResults[pageIdx] = await ReviewPageAndTrackAsync(job, pageNumber, enrichedContent, filePaths, ct);
            }

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
        Interlocked.Increment(ref job.CompletedPages);
        return result;
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
                result = await _pageReviewer.ReviewPageAsync(pageNumber, enrichedContent, ct).ConfigureAwait(false);
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
