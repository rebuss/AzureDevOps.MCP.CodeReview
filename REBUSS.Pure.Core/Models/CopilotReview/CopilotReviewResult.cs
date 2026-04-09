namespace REBUSS.Pure.Core.Models.CopilotReview;

/// <summary>
/// Aggregate result of a full PR-level Copilot review across all pages.
/// Cached in <see cref="Services.CopilotReview.ICopilotReviewOrchestrator"/>'s
/// PR-keyed dictionary for the lifetime of the MCP server process (Clarification Q2).
/// </summary>
public sealed record CopilotReviewResult
{
    public required int PrNumber { get; init; }
    public required IReadOnlyList<CopilotPageReviewResult> PageReviews { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }

    public int TotalPages => PageReviews.Count;
    public int SucceededPages => PageReviews.Count(r => r.Succeeded);
    public int FailedPages => PageReviews.Count(r => !r.Succeeded);
}
