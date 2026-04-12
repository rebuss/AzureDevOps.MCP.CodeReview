namespace REBUSS.Pure.Services.LocalReview;

/// <summary>
/// Non-blocking read-only view of an in-flight or completed local enrichment job.
/// Mirrors <see cref="PrEnrichment.PrEnrichmentJobSnapshot"/>.
/// </summary>
public sealed record LocalEnrichmentJobSnapshot
{
    public required string Scope { get; init; }
    public required LocalEnrichmentStatus Status { get; init; }
    public LocalEnrichmentResult? Result { get; init; }
    public LocalEnrichmentFailure? Failure { get; init; }
}
