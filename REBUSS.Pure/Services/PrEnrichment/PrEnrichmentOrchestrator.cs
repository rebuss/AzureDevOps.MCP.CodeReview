using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Services.ResponsePacking;
using REBUSS.Pure.Tools.Shared;

namespace REBUSS.Pure.Services.PrEnrichment;

/// <inheritdoc cref="IPrEnrichmentOrchestrator"/>
public sealed class PrEnrichmentOrchestrator : IPrEnrichmentOrchestrator
{
    private readonly IPullRequestDiffCache _diffCache;
    private readonly ITokenEstimator _tokenEstimator;
    private readonly IFileClassifier _fileClassifier;
    private readonly ICodeProcessor _codeProcessor;
    private readonly IPageAllocator _pageAllocator;
    private readonly ILogger<PrEnrichmentOrchestrator> _logger;
    private readonly CancellationToken _shutdownToken;

    private readonly ConcurrentDictionary<int, PrEnrichmentJob> _jobs = new();
    private readonly object _lock = new();

    public PrEnrichmentOrchestrator(
        IPullRequestDiffCache diffCache,
        ITokenEstimator tokenEstimator,
        IFileClassifier fileClassifier,
        ICodeProcessor codeProcessor,
        IPageAllocator pageAllocator,
        IHostApplicationLifetime lifetime,
        ILogger<PrEnrichmentOrchestrator> logger)
    {
        _diffCache = diffCache;
        _tokenEstimator = tokenEstimator;
        _fileClassifier = fileClassifier;
        _codeProcessor = codeProcessor;
        _pageAllocator = pageAllocator;
        _logger = logger;
        _shutdownToken = lifetime.ApplicationStopping;
    }

    public void TriggerEnrichment(int prNumber, string headSha, int safeBudgetTokens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headSha);

        lock (_lock)
        {
            if (_jobs.TryGetValue(prNumber, out var existing))
            {
                // Idempotent: same SHA and not failed → reuse the existing job.
                if (string.Equals(existing.HeadSha, headSha, StringComparison.OrdinalIgnoreCase)
                    && existing.Status != PrEnrichmentStatus.Failed)
                {
                    return;
                }

                // Different SHA, or prior job failed → supersede.
                try { existing.Cts.Cancel(); }
                catch (ObjectDisposedException) { /* already disposed */ }
            }

            var jobCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
            var job = new PrEnrichmentJob
            {
                PrNumber = prNumber,
                HeadSha = headSha,
                Cts = jobCts,
                StartedAt = DateTimeOffset.UtcNow,
            };
            _jobs[prNumber] = job;

            // IMPORTANT: pass CancellationToken.None to Task.Run itself; the body
            // honors job.Cts.Token internally. The caller's ct is not in scope here.
            job.ResultTask = Task.Run(() => BackgroundBodyAsync(job, safeBudgetTokens), CancellationToken.None);
        }
    }

    public Task<PrEnrichmentResult> WaitForEnrichmentAsync(int prNumber, CancellationToken ct)
    {
        if (!_jobs.TryGetValue(prNumber, out var job))
            throw new InvalidOperationException($"No enrichment job for PR #{prNumber}");

        // Caller's ct only governs the wait — the background body keeps running.
        return job.ResultTask.WaitAsync(ct);
    }

    public PrEnrichmentJobSnapshot? TryGetSnapshot(int prNumber)
    {
        if (!_jobs.TryGetValue(prNumber, out var job))
            return null;

        lock (_lock)
        {
            return new PrEnrichmentJobSnapshot
            {
                PrNumber = job.PrNumber,
                HeadSha = job.HeadSha,
                Status = job.Status,
                Result = job.Result,
                Failure = job.Failure,
            };
        }
    }

    private async Task<PrEnrichmentResult> BackgroundBodyAsync(PrEnrichmentJob job, int safeBudgetTokens)
    {
        var ct = job.Cts.Token;
        _logger.LogInformation(
            "PR {PrNumber} enrichment job started (sha {HeadSha}, budget {SafeBudgetTokens})",
            job.PrNumber, job.HeadSha, safeBudgetTokens);

        try
        {
            lock (_lock) { job.Status = PrEnrichmentStatus.FetchingDiff; }
            var diff = await _diffCache.GetOrFetchDiffAsync(job.PrNumber, job.HeadSha, ct);

            ct.ThrowIfCancellationRequested();
            lock (_lock) { job.Status = PrEnrichmentStatus.Enriching; }

            var (candidates, enrichedByPath) = await FileTokenMeasurement.BuildEnrichedCandidatesAsync(
                diff, _tokenEstimator, _fileClassifier, _codeProcessor, ct);

            candidates.Sort(PackingPriorityComparer.Instance);
            var allocation = _pageAllocator.Allocate(candidates, safeBudgetTokens);

            var result = new PrEnrichmentResult
            {
                PrNumber = job.PrNumber,
                HeadSha = job.HeadSha,
                SortedCandidates = candidates,
                EnrichedByPath = enrichedByPath,
                Allocation = allocation,
                SafeBudgetTokens = safeBudgetTokens,
                CompletedAt = DateTimeOffset.UtcNow,
            };

            lock (_lock)
            {
                job.Result = result;
                job.Status = PrEnrichmentStatus.Ready;
            }

            _logger.LogInformation(
                "PR {PrNumber} enrichment completed: {FileCount} file(s), {PageCount} page(s)",
                job.PrNumber, candidates.Count, allocation.TotalPages);

            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Job was superseded or process is shutting down. Don't mark as Failed.
            _logger.LogInformation("PR {PrNumber} enrichment cancelled", job.PrNumber);
            throw;
        }
        catch (Exception ex)
        {
            var failure = PrEnrichmentFailure.From(ex);
            lock (_lock)
            {
                job.Failure = failure;
                job.Status = PrEnrichmentStatus.Failed;
            }
            _logger.LogWarning(
                ex,
                "PR {PrNumber} enrichment failed: {ExceptionType}",
                job.PrNumber, failure.ExceptionTypeName);
            throw;
        }
    }
}
