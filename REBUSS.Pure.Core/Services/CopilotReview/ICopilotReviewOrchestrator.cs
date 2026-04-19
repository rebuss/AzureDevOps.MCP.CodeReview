using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Models.CopilotReview;

namespace REBUSS.Pure.Core.Services.CopilotReview;

/// <summary>
/// Coordinates the server-side Copilot review of every page of enriched content.
/// Source-agnostic — serves both PR reviews and local self-reviews. Cache key is
/// an opaque string (<c>reviewKey</c>) whose format encodes the source type
/// (e.g. <c>pr:42</c> or <c>local:staged:/repo</c>) to prevent collisions.
/// </summary>
public interface ICopilotReviewOrchestrator
{
    /// <summary>
    /// Idempotent: starts a background Copilot review for the given key if one is not
    /// already running or completed. Safe to call from any tool handler after enrichment
    /// is ready.
    /// </summary>
    void TriggerReview(string reviewKey, IEnrichmentResult enrichmentResult);

    /// <summary>
    /// Awaits the completion of an in-flight or already-completed review. Cancellation
    /// on <paramref name="ct"/> returns control to the caller promptly; background work
    /// continues so a subsequent call observes the same result.
    /// </summary>
    Task<CopilotReviewResult> WaitForReviewAsync(string reviewKey, CancellationToken ct);

    /// <summary>
    /// Non-blocking read of the current state. Returns <c>null</c> if no review has been
    /// triggered for this key in the current process lifetime.
    /// </summary>
    CopilotReviewSnapshot? TryGetSnapshot(string reviewKey);
}
