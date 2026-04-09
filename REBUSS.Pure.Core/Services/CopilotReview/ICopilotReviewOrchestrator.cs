using REBUSS.Pure.Core.Models.CopilotReview;

namespace REBUSS.Pure.Core.Services.CopilotReview;

/// <summary>
/// Coordinates the server-side Copilot review of every page of a PR's enriched content.
/// Mirrors the <c>PrEnrichmentOrchestrator</c> trigger/wait/snapshot pattern. Cache key
/// is <c>prNumber</c> only (Clarification Q2) — configuration changes do not invalidate
/// cached results within a single MCP server process.
/// </summary>
/// <remarks>
/// The orchestrator accepts an opaque <see cref="object"/> <c>enrichmentResult</c>
/// parameter so that <c>REBUSS.Pure.Core</c> does not depend on the
/// <c>PrEnrichmentResult</c> type from <c>REBUSS.Pure</c>. The production implementation
/// casts to the concrete type internally.
/// </remarks>
public interface ICopilotReviewOrchestrator
{
    /// <summary>
    /// Idempotent: starts a background Copilot review for the PR if one is not already
    /// running or completed. Safe to call from the tool handler after enrichment is ready.
    /// </summary>
    void TriggerReview(int prNumber, object enrichmentResult);

    /// <summary>
    /// Awaits the completion of an in-flight or already-completed review. Cancellation
    /// on <paramref name="ct"/> returns control to the caller promptly; background work
    /// continues so a subsequent call observes the same result (FR-011).
    /// </summary>
    Task<CopilotReviewResult> WaitForReviewAsync(int prNumber, CancellationToken ct);

    /// <summary>
    /// Non-blocking read of the current state. Returns <c>null</c> if no review has been
    /// triggered for this PR in the current process lifetime.
    /// </summary>
    CopilotReviewSnapshot? TryGetSnapshot(int prNumber);
}
