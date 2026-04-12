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
/// </summary>
internal sealed class PrEnrichmentJob
{
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
}
