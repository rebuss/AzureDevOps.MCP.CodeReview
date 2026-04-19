namespace REBUSS.Pure.Core.Models.CopilotReview;

/// <summary>
/// Aggregate result of a Copilot review across all pages. Source-agnostic — serves
/// both PR reviews (key format <c>pr:{N}</c>) and local self-reviews
/// (key format <c>local:{scope}:{repoRoot}</c>).
/// Cached in <see cref="Services.CopilotReview.ICopilotReviewOrchestrator"/>'s
/// string-keyed dictionary for the lifetime of the MCP server process.
/// </summary>
public sealed record CopilotReviewResult
{
    public required string ReviewKey { get; init; }
    public required IReadOnlyList<CopilotPageReviewResult> PageReviews { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }

    public int TotalPages => PageReviews.Count;
    public int SucceededPages => PageReviews.Count(r => r.Succeeded);
    public int FailedPages => PageReviews.Count(r => !r.Succeeded);
}
