namespace REBUSS.Pure.Services.PrEnrichment;

/// <summary>
/// Read-only view of a <see cref="PrEnrichmentJob"/> for handlers that need to
/// inspect state without awaiting. Returned by
/// <see cref="IPrEnrichmentOrchestrator.TryGetSnapshot(int)"/>.
/// </summary>
public sealed record PrEnrichmentJobSnapshot
{
    public required int PrNumber { get; init; }
    public required string HeadSha { get; init; }
    public required PrEnrichmentStatus Status { get; init; }
    public PrEnrichmentResult? Result { get; init; }
    public PrEnrichmentFailure? Failure { get; init; }
}
