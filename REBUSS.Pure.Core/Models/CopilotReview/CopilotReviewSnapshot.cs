namespace REBUSS.Pure.Core.Models.CopilotReview;

/// <summary>
/// Read-only snapshot of a single Copilot review job's state, returned by
/// <see cref="Services.CopilotReview.ICopilotReviewOrchestrator.TryGetSnapshot"/>.
/// Mirrors the <c>PrEnrichmentJobSnapshot</c> pattern.
/// </summary>
public sealed record CopilotReviewSnapshot
{
    public required int PrNumber { get; init; }
    public required CopilotReviewStatus Status { get; init; }
    public CopilotReviewResult? Result { get; init; }
    public string? ErrorMessage { get; init; }
}
