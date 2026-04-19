namespace REBUSS.Pure.Services.LocalReview;

/// <summary>
/// Sanitized failure details for a failed local enrichment job.
/// Mirrors <see cref="PrEnrichment.PrEnrichmentFailure"/>.
/// </summary>
public sealed record LocalEnrichmentFailure
{
    public required string ExceptionTypeName { get; init; }
    public required string SanitizedMessage { get; init; }
}
