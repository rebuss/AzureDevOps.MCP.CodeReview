using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Services.ResponsePacking;
using REBUSS.Pure.Tools.Shared;

namespace REBUSS.Pure.Services.LocalReview;

/// <inheritdoc cref="ILocalEnrichmentOrchestrator"/>
internal sealed class LocalEnrichmentOrchestrator : ILocalEnrichmentOrchestrator
{
    private readonly ILocalReviewProvider _localProvider;
    private readonly ITokenEstimator _tokenEstimator;
    private readonly IFileClassifier _fileClassifier;
    private readonly ICodeProcessor _codeProcessor;
    private readonly IPageAllocator _pageAllocator;
    private readonly ILogger<LocalEnrichmentOrchestrator> _logger;
    private readonly CancellationToken _shutdownToken;

    private readonly ConcurrentDictionary<string, LocalEnrichmentJob> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public LocalEnrichmentOrchestrator(
        ILocalReviewProvider localProvider,
        ITokenEstimator tokenEstimator,
        IFileClassifier fileClassifier,
        ICodeProcessor codeProcessor,
        IPageAllocator pageAllocator,
        IHostApplicationLifetime lifetime,
        ILogger<LocalEnrichmentOrchestrator> logger)
    {
        _localProvider = localProvider;
        _tokenEstimator = tokenEstimator;
        _fileClassifier = fileClassifier;
        _codeProcessor = codeProcessor;
        _pageAllocator = pageAllocator;
        _logger = logger;
        _shutdownToken = lifetime.ApplicationStopping;
    }

    public void TriggerEnrichment(string scope, int safeBudgetTokens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        lock (_lock)
        {
            // Idempotent — same scope and not failed → reuse the existing job.
            if (_jobs.TryGetValue(scope, out var existing) && existing.Status != LocalEnrichmentStatus.Failed)
                return;

            var job = new LocalEnrichmentJob
            {
                Scope = scope,
                Completion = new TaskCompletionSource<LocalEnrichmentResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously),
            };
            _jobs[scope] = job;

            _ = Task.Run(() => BackgroundBodyAsync(job, safeBudgetTokens), CancellationToken.None);
        }
    }

    public Task<LocalEnrichmentResult> WaitForEnrichmentAsync(string scope, CancellationToken ct)
    {
        if (!_jobs.TryGetValue(scope, out var job))
            throw new InvalidOperationException($"No local enrichment job for scope '{scope}'");

        return job.Completion.Task.WaitAsync(ct);
    }

    public LocalEnrichmentJobSnapshot? TryGetSnapshot(string scope)
    {
        if (!_jobs.TryGetValue(scope, out var job))
            return null;

        lock (_lock)
        {
            return new LocalEnrichmentJobSnapshot
            {
                Scope = job.Scope,
                Status = job.Status,
                Result = job.Result,
                Failure = job.Failure,
            };
        }
    }

    private async Task BackgroundBodyAsync(LocalEnrichmentJob job, int safeBudgetTokens)
    {
        var ct = _shutdownToken;
        var parsedScope = LocalReviewScope.Parse(job.Scope);

        _logger.LogInformation(
            "Local enrichment started (scope '{Scope}', budget {SafeBudgetTokens})",
            job.Scope, safeBudgetTokens);

        try
        {
            lock (_lock) { job.Status = LocalEnrichmentStatus.FetchingDiff; }

            var localFiles = await _localProvider.GetFilesAsync(parsedScope, ct);

            // Fetch every file's diff in parallel.
            var fetchTasks = localFiles.Files
                .Select(f => _localProvider.GetFileDiffAsync(f.Path, parsedScope, ct))
                .ToList();
            await Task.WhenAll(fetchTasks);

            var diffByPath = new Dictionary<string, Core.Models.FileChange>(StringComparer.OrdinalIgnoreCase);
            foreach (var task in fetchTasks)
            {
                var fc = task.Result.Files.FirstOrDefault();
                if (fc != null)
                    diffByPath[fc.Path] = fc;
            }

            var aggregatedDiff = new Core.Models.PullRequestDiff
            {
                Files = diffByPath.Values.ToList()
            };

            ct.ThrowIfCancellationRequested();
            lock (_lock) { job.Status = LocalEnrichmentStatus.Enriching; }

            var (candidates, enrichedByPath) = await FileTokenMeasurement.BuildEnrichedCandidatesAsync(
                aggregatedDiff, _tokenEstimator, _fileClassifier, _codeProcessor, ct);

            candidates.Sort(PackingPriorityComparer.Instance);
            var allocation = _pageAllocator.Allocate(candidates, safeBudgetTokens);

            var result = new LocalEnrichmentResult
            {
                RepositoryRoot = localFiles.RepositoryRoot,
                CurrentBranch = localFiles.CurrentBranch ?? string.Empty,
                Scope = job.Scope,
                SortedCandidates = candidates,
                EnrichedByPath = enrichedByPath,
                Allocation = allocation,
                SafeBudgetTokens = safeBudgetTokens,
                CompletedAt = DateTimeOffset.UtcNow,
            };

            lock (_lock)
            {
                job.Result = result;
                job.Status = LocalEnrichmentStatus.Ready;
            }

            _logger.LogInformation(
                "Local enrichment completed (scope '{Scope}'): {FileCount} file(s), {PageCount} page(s)",
                job.Scope, candidates.Count, allocation.TotalPages);

            job.Completion.TrySetResult(result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Local enrichment cancelled (scope '{Scope}')", job.Scope);
            lock (_lock) { job.Status = LocalEnrichmentStatus.Failed; }
            job.Completion.TrySetCanceled(ct);
        }
        catch (Exception ex)
        {
            var failure = new LocalEnrichmentFailure
            {
                ExceptionTypeName = ex.GetType().Name,
                SanitizedMessage = ex.Message,
            };
            lock (_lock)
            {
                job.Failure = failure;
                job.Status = LocalEnrichmentStatus.Failed;
            }
            _logger.LogWarning(ex, "Local enrichment failed (scope '{Scope}'): {ExceptionType}",
                job.Scope, failure.ExceptionTypeName);
            job.Completion.TrySetException(ex);
        }
    }

    private sealed class LocalEnrichmentJob
    {
        public required string Scope { get; init; }
        public LocalEnrichmentStatus Status { get; set; } = LocalEnrichmentStatus.FetchingDiff;
        public LocalEnrichmentResult? Result { get; set; }
        public LocalEnrichmentFailure? Failure { get; set; }
        public required TaskCompletionSource<LocalEnrichmentResult> Completion { get; init; }
    }
}
