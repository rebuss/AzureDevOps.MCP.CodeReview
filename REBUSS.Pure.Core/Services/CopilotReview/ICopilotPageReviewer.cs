using REBUSS.Pure.Core.Models.CopilotReview;

namespace REBUSS.Pure.Core.Services.CopilotReview;

/// <summary>
/// Reviews a single page of enriched PR content via GitHub Copilot and returns the
/// outcome. Implementations MUST NEVER throw — failures are returned as
/// <see cref="CopilotPageReviewResult.Failure"/> so the orchestrator's retry loop
/// (research.md Decision 3) can count attempts uniformly regardless of whether the
/// underlying SDK call succeeded, returned empty, or threw.
/// </summary>
public interface ICopilotPageReviewer
{
    /// <summary>
    /// Sends <paramref name="enrichedPageContent"/> plus the embedded review instruction
    /// template to Copilot and collects the response. On success, returns
    /// <see cref="CopilotPageReviewResult.Success"/>; on any failure (empty response,
    /// session error, thrown exception), returns
    /// <see cref="CopilotPageReviewResult.Failure"/> with an empty
    /// <c>FailedFilePaths</c> list — the orchestrator fills in the file paths because
    /// only the orchestrator knows which files were on the page.
    /// </summary>
    Task<CopilotPageReviewResult> ReviewPageAsync(
        int pageNumber,
        string enrichedPageContent,
        CancellationToken ct = default);
}
