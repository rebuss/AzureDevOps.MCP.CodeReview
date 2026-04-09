namespace REBUSS.Pure.Core.Models.CopilotReview;

/// <summary>
/// Status of a single PR-keyed Copilot review job owned by
/// <c>ICopilotReviewOrchestrator</c>. Transitions are one-way per data-model.md §4:
/// <c>Reviewing → Ready</c> or <c>Reviewing → Failed</c>. <c>Failed</c> is only used for
/// orchestrator-level fatal conditions; per-page failures are encoded inside
/// <see cref="CopilotPageReviewResult"/>.
/// </summary>
public enum CopilotReviewStatus
{
    Reviewing = 0,
    Ready = 1,
    Failed = 2,
}
