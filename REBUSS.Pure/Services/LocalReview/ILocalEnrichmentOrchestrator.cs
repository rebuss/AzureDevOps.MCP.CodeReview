namespace REBUSS.Pure.Services.LocalReview;

/// <summary>
/// Owns background enrichment of local git changes. Mirrors the
/// <see cref="PrEnrichment.IPrEnrichmentOrchestrator"/> trigger/wait/snapshot pattern.
/// Cache key is the scope string (e.g. <c>"staged"</c>, <c>"working-tree"</c>).
/// </summary>
public interface ILocalEnrichmentOrchestrator
{
    /// <summary>
    /// Start (or join) a background enrichment job for the given scope.
    /// Returns immediately. Idempotent for the same <paramref name="scope"/>.
    /// </summary>
    void TriggerEnrichment(string scope, int safeBudgetTokens);

    /// <summary>
    /// Awaits the result of an in-flight or completed enrichment job, bounded
    /// by <paramref name="ct"/>. The background body itself is NOT cancelled
    /// when <paramref name="ct"/> fires.
    /// </summary>
    Task<LocalEnrichmentResult> WaitForEnrichmentAsync(string scope, CancellationToken ct);

    /// <summary>
    /// Returns the current job state for a scope without awaiting, or <c>null</c>
    /// if no job exists.
    /// </summary>
    LocalEnrichmentJobSnapshot? TryGetSnapshot(string scope);
}
