using Microsoft.Extensions.Options;
using REBUSS.Pure.Core.Models.CopilotReview;
using REBUSS.Pure.Core.Services.CopilotReview;
using System.Collections.Concurrent;

namespace REBUSS.Pure.Services.CopilotReview;

/// <summary>
/// Owns the in-memory registry of <see cref="AgentReviewJob"/> entries plus the lock
/// guarding terminal-state transitions and snapshot reads. Provides idempotent
/// registration (with an opportunistic TTL sweep), narrow helpers for terminal-state
/// updates (<see cref="CompleteUnderLock"/>) and observability projections
/// (<see cref="Snapshot"/>). The orchestrator holds this as a singleton collaborator
/// so the lock and the dictionary stay encapsulated here.
/// </summary>
internal sealed class AgentReviewJobRegistry
{
    private readonly ConcurrentDictionary<string, AgentReviewJob> _jobs = new();
    private readonly object _lock = new();
    private readonly IOptions<CopilotReviewOptions> _options;

    public AgentReviewJobRegistry(IOptions<CopilotReviewOptions> options)
    {
        _options = options;
    }

    /// <summary>
    /// Registers a fresh job for <paramref name="reviewKey"/> if none exists. Performs
    /// an opportunistic TTL sweep first to keep <c>_jobs</c> from growing unboundedly
    /// in a long-running MCP server. Returns the newly created job, or <c>null</c> when
    /// a job for this key is already in flight (idempotency — the caller will observe
    /// the same result via the existing job's <see cref="AgentReviewJob.Completion"/>).
    /// <para>
    /// <paramref name="backgroundBodyFactory"/> is invoked atomically inside the
    /// registry lock — its <see cref="Task"/> result is assigned to
    /// <see cref="AgentReviewJob.BackgroundTask"/> before this method returns, so a
    /// concurrent <see cref="AgentReviewOrchestrator.DisposeAsync"/> draining
    /// <c>_jobs</c> via <see cref="All"/> can never observe a freshly-registered job
    /// with <c>BackgroundTask == null</c>. The factory MUST be cheap (queue-only) —
    /// e.g. <see cref="Task.Run(Func{Task})"/> — because the lock is held while it
    /// runs; doing real work here would serialize all other registrations behind it.
    /// </para>
    /// </summary>
    public AgentReviewJob? TryRegister(string reviewKey, Func<AgentReviewJob, Task> backgroundBodyFactory)
    {
        lock (_lock)
        {
            SweepStaleJobs();

            if (_jobs.ContainsKey(reviewKey))
                return null;

            var job = new AgentReviewJob
            {
                ReviewKey = reviewKey,
                Completion = new TaskCompletionSource<AgentReviewResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously),
            };
            _jobs[reviewKey] = job;

            // Assigned inside the lock so DisposeAsync can never see the job before
            // BackgroundTask is set — graceful shutdown drain stays correct.
            job.BackgroundTask = backgroundBodyFactory(job);
            return job;
        }
    }

    public bool TryGet(string reviewKey, out AgentReviewJob job)
        => _jobs.TryGetValue(reviewKey, out job!);

    /// <summary>All registered jobs (alive or terminal). Used by shutdown drain.</summary>
    public ICollection<AgentReviewJob> All => _jobs.Values;

    /// <summary>
    /// Atomically transitions <paramref name="job"/> to a terminal state (Ready / Failed)
    /// while holding the registry lock — keeps <see cref="Snapshot"/> readers from
    /// observing torn writes across <c>Status</c> / <c>Result</c> / <c>ErrorMessage</c> /
    /// <c>CompletedAt</c>.
    /// <para>
    /// Invariant: <c>Ready</c> requires a non-null <paramref name="result"/> — every
    /// production caller transitioning to <c>Ready</c> has produced an
    /// <see cref="AgentReviewResult"/>, and consumers reading a <c>Ready</c> snapshot
    /// expect <c>Result</c> to be present. <c>Failed</c> accepts a null result (and
    /// usually a non-null <paramref name="errorMessage"/>) — the OCE / general-catch
    /// branches in <see cref="AgentReviewOrchestrator"/> finalize without a result.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="status"/> is <see cref="AgentReviewStatus.Ready"/>
    /// and <paramref name="result"/> is null.
    /// </exception>
    public void CompleteUnderLock(
        AgentReviewJob job,
        AgentReviewStatus status,
        AgentReviewResult? result,
        string? errorMessage)
    {
        if (status == AgentReviewStatus.Ready)
            ArgumentNullException.ThrowIfNull(result);

        lock (_lock)
        {
            job.Status = status;
            job.Result = result;
            job.ErrorMessage = errorMessage;
            job.CompletedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Sets the page total under the registry lock so concurrent <see cref="Snapshot"/>
    /// readers see a coherent value alongside <c>Status</c>.
    /// </summary>
    public void SetTotalPagesUnderLock(AgentReviewJob job, int totalPages)
    {
        lock (_lock)
        {
            job.TotalPages = totalPages;
        }
    }

    /// <summary>
    /// Projects the job's volatile fields into an <see cref="AgentReviewSnapshot"/>
    /// under the lock so Status / Result / ErrorMessage / TotalPages are consistent
    /// with each other (CompletedPages and CurrentActivity are read with their own
    /// memory-barriered primitives).
    /// </summary>
    public AgentReviewSnapshot? Snapshot(string reviewKey)
    {
        if (!_jobs.TryGetValue(reviewKey, out var job))
            return null;

        lock (_lock)
        {
            return new AgentReviewSnapshot
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

    /// <summary>
    /// Evicts terminal jobs (Ready / Failed) whose <c>CompletedAt</c> is older than
    /// <see cref="CopilotReviewOptions.JobRetentionMinutes"/>. Called under <c>_lock</c>
    /// from <see cref="TryRegister"/> — opportunistic, no timer thread needed.
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
}
