using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Models.CopilotReview;
using REBUSS.Pure.Core.Services.CopilotReview;
using REBUSS.Pure.Properties;

namespace REBUSS.Pure.Services.CopilotReview;

/// <summary>
/// Coordinates the server-side Copilot review of every page of enriched content.
/// Source-agnostic — serves both PR reviews and local self-reviews via string-based
/// review keys. Mirrors the <c>PrEnrichmentOrchestrator</c> trigger/wait/snapshot pattern.
/// <para>
/// Composes three focused collaborators:
/// <list type="bullet">
///   <item><see cref="AgentReviewJobRegistry"/> — owns the in-memory job dictionary,
///         lock, idempotent registration, TTL sweep, and snapshot projection.</item>
///   <item><see cref="PageReviewExecutor"/> — owns the per-page work: build enriched
///         payload, batched parallel dispatch (<see cref="CopilotReviewOptions.MaxConcurrentPages"/>),
///         3-attempt retry, atomic <c>CompletedPages</c> increment.</item>
///   <item><see cref="FindingValidationPipeline"/> — owns the post-page Feature 021
///         validation pass (parse → resolve scopes → validate → map back). Opt-in via
///         <see cref="FindingValidationPipeline.IsEnabled"/>.</item>
/// </list>
/// The orchestrator itself owns the <c>IHostApplicationLifetime</c> token, the
/// background-task lifetime (including the dispose-time drain), and the terminal-state
/// transitions written through the registry's <see cref="AgentReviewJobRegistry.CompleteUnderLock"/>
/// helper.
/// </para>
/// </summary>
internal sealed class AgentReviewOrchestrator : IAgentReviewOrchestrator, IAsyncDisposable
{
    private readonly AgentReviewJobRegistry _registry;
    private readonly PageReviewExecutor _pageExecutor;
    private readonly FindingValidationPipeline _validationPipeline;
    private readonly IPageAllocator _pageAllocator;
    private readonly IOptions<CopilotReviewOptions> _options;
    private readonly ILogger<AgentReviewOrchestrator> _logger;
    private readonly CancellationToken _shutdownToken;

    public AgentReviewOrchestrator(
        AgentReviewJobRegistry registry,
        PageReviewExecutor pageExecutor,
        FindingValidationPipeline validationPipeline,
        IPageAllocator pageAllocator,
        IOptions<CopilotReviewOptions> options,
        IHostApplicationLifetime lifetime,
        ILogger<AgentReviewOrchestrator> logger)
    {
        _registry = registry;
        _pageExecutor = pageExecutor;
        _validationPipeline = validationPipeline;
        _pageAllocator = pageAllocator;
        _options = options;
        _logger = logger;
        _shutdownToken = lifetime.ApplicationStopping;
    }

    public void TriggerReview(string reviewKey, IEnrichmentResult enrichmentResult)
    {
        ArgumentException.ThrowIfNullOrEmpty(reviewKey);
        ArgumentNullException.ThrowIfNull(enrichmentResult);

        // Idempotent — TryRegister returns null when a job for this key is already in
        // flight; the caller will observe the same result via WaitForReviewAsync.
        // The body factory is invoked atomically inside the registry lock so that
        // BackgroundTask is set before any concurrent DisposeAsync iterates _jobs;
        // otherwise the drain would skip a freshly-registered job with a null handle
        // and the body could outlive shutdown without updating Status / signalling
        // pending waiters.
        // Task.Run uses CancellationToken.None — the body honours _shutdownToken
        // internally via its own catch(OCE) branch.
        _ = _registry.TryRegister(
            reviewKey,
            j => Task.Run(() => BackgroundBodyAsync(j, enrichmentResult), CancellationToken.None));
    }

    /// <summary>
    /// Registered by DI on host shutdown (singleton lifetime). The <c>_shutdownToken</c>
    /// (ApplicationStopping) has already fired by the time this runs, so each background
    /// body is either in its catch(OCE) branch or about to be. We await the in-flight tasks
    /// with a bounded timeout so shutdown cannot hang on a misbehaving body while still
    /// giving the graceful-cancellation path a chance to finish — update Status, publish
    /// ErrorMessage, and signal pending waiters through <see cref="AgentReviewJob.Completion"/>.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        var tasks = _registry.All
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

    public Task<AgentReviewResult> WaitForReviewAsync(string reviewKey, CancellationToken ct)
    {
        if (!_registry.TryGet(reviewKey, out var job))
            throw new InvalidOperationException($"No Copilot review job for key '{reviewKey}'");

        // Caller's ct only governs the wait; the background body keeps running (FR-011).
        return job.Completion.Task.WaitAsync(ct);
    }

    public AgentReviewSnapshot? TryGetSnapshot(string reviewKey) => _registry.Snapshot(reviewKey);

    private async Task BackgroundBodyAsync(AgentReviewJob job, IEnrichmentResult enrichment)
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

            _registry.SetTotalPagesUnderLock(job, allocation.TotalPages);
            job.CurrentActivity = $"Allocated {allocation.TotalPages} pages — reviewing in parallel";

            // Empty-allocation fast path (edge case: empty PR / zero pages).
            if (allocation.TotalPages == 0)
            {
                var emptyResult = new AgentReviewResult
                {
                    ReviewKey = job.ReviewKey,
                    PageReviews = Array.Empty<AgentPageReviewResult>(),
                    CompletedAt = DateTimeOffset.UtcNow,
                };
                _registry.CompleteUnderLock(job, AgentReviewStatus.Ready, emptyResult, errorMessage: null);
                job.Completion.TrySetResult(emptyResult);
                return;
            }

            var pageResults = await _pageExecutor.ExecuteAllAsync(job, allocation, enrichment, ct);

            // Feature 021: consolidated finding validation across all pages.
            // Runs once after all pages complete (not per-page) to minimize Copilot calls.
            if (_validationPipeline.IsEnabled)
            {
                try
                {
                    await _validationPipeline.RunAsync(job, pageResults, ct).ConfigureAwait(false);
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

            var result = new AgentReviewResult
            {
                ReviewKey = job.ReviewKey,
                PageReviews = pageResults,
                CompletedAt = DateTimeOffset.UtcNow,
            };

            // Publish a final progress activity — the waiter surfaces this as the last
            // IDE notification so the user sees "Review complete" rather than the review
            // flipping silent right after "N/N pages complete".
            job.CurrentActivity = $"Review complete — {result.SucceededPages}/{result.TotalPages} pages succeeded";

            _registry.CompleteUnderLock(job, AgentReviewStatus.Ready, result, errorMessage: null);

            _logger.LogInformation(
                Resources.LogCopilotReviewCompleted,
                job.ReviewKey, result.SucceededPages, result.TotalPages);

            job.Completion.TrySetResult(result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Copilot review for '{ReviewKey}' cancelled by shutdown", job.ReviewKey);
            _registry.CompleteUnderLock(job, AgentReviewStatus.Failed, result: null, errorMessage: "cancelled");
            job.Completion.TrySetCanceled(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, Resources.LogCopilotReviewFailed, job.ReviewKey, ex.Message);
            _registry.CompleteUnderLock(job, AgentReviewStatus.Failed, result: null, errorMessage: ex.Message);
            job.Completion.TrySetException(ex);
        }
    }
}
