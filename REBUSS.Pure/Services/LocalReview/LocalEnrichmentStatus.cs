namespace REBUSS.Pure.Services.LocalReview;

/// <summary>
/// Tracks the lifecycle of a background local-change enrichment job.
/// Mirrors <see cref="PrEnrichment.PrEnrichmentStatus"/> by design.
/// </summary>
public enum LocalEnrichmentStatus
{
    /// <summary>Fetching file list and per-file diffs from git.</summary>
    FetchingDiff = 0,

    /// <summary>Running <c>FileTokenMeasurement.BuildEnrichedCandidatesAsync</c> (Roslyn analysis).</summary>
    Enriching = 1,

    /// <summary>Enrichment complete — <see cref="LocalEnrichmentResult"/> available.</summary>
    Ready = 2,

    /// <summary>Enrichment failed — <see cref="LocalEnrichmentFailure"/> available.</summary>
    Failed = 3,
}
