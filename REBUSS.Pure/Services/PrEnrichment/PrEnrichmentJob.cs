namespace REBUSS.Pure.Services.PrEnrichment;

/// <summary>
/// Per-PR mutable state record. Lives only as values inside
/// <see cref="PrEnrichmentOrchestrator"/>'s private dictionary; never exposed
/// outside the orchestrator (snapshots are projected via
/// <see cref="PrEnrichmentJobSnapshot"/>).
///
/// This is the documented Principle VI deviation — mutable instance fields on
/// a singleton-owned object — mirroring the precedent set by
/// <c>RepositoryDownloadOrchestrator</c>.
/// <para>
/// Implements <see cref="IDisposable"/> because <see cref="Cts"/> is a linked
/// <see cref="CancellationTokenSource"/> that holds kernel wait handles. The
/// orchestrator schedules <see cref="Dispose"/> on background-task completion
/// so handles are released for every job, not leaked for process lifetime.
/// </para>
/// </summary>
internal sealed class PrEnrichmentJob : IDisposable
{
    private int _disposed;

    public required int PrNumber { get; init; }
    public required string HeadSha { get; init; }
    public required CancellationTokenSource Cts { get; init; }
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>The awaitable representing background completion. Set once when the job starts.</summary>
    public Task<PrEnrichmentResult>? ResultTask { get; set; }

    /// <summary>Mutated under the orchestrator's lock.</summary>
    public PrEnrichmentStatus Status { get; set; } = PrEnrichmentStatus.FetchingDiff;

    /// <summary>Populated when <see cref="Status"/> transitions to <see cref="PrEnrichmentStatus.Ready"/>.</summary>
    public PrEnrichmentResult? Result { get; set; }

    /// <summary>Populated when <see cref="Status"/> transitions to <see cref="PrEnrichmentStatus.Failed"/>.</summary>
    public PrEnrichmentFailure? Failure { get; set; }

    /// <summary>
    /// Releases the <see cref="Cts"/> kernel handles. Idempotent — safe to call
    /// from both a task-completion continuation and shutdown cleanup.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Cts.Dispose();
    }
}
