namespace REBUSS.Pure.Services.PrEnrichment;

/// <summary>
/// Lifecycle of a single (PR, head SHA) enrichment job tracked by
/// <see cref="IPrEnrichmentOrchestrator"/>.
/// </summary>
public enum PrEnrichmentStatus
{
    /// <summary>Background body has started; raw diff fetch is in flight.</summary>
    FetchingDiff = 0,

    /// <summary>Diff fetched; <c>BuildEnrichedCandidatesAsync</c> is running.</summary>
    Enriching = 1,

    /// <summary>Enriched candidates and per-file enriched text are available.</summary>
    Ready = 2,

    /// <summary>Background body threw; <c>Failure</c> carries sanitized context.</summary>
    Failed = 3,
}
