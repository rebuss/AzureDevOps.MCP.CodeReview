namespace REBUSS.Pure.Services.ReviewSession;

/// <summary>
/// Lifecycle states for a single file within a stateful PR review session.
/// See <c>specs/012-review-session-mvp/data-model.md</c> for the full state machine.
/// </summary>
public enum ReviewItemStatus
{
    /// <summary>Not yet delivered to the agent.</summary>
    Pending,

    /// <summary>One or more chunks of the file have been delivered, but not the final chunk.</summary>
    DeliveredPartial,

    /// <summary>The whole file (or its final chunk) has been delivered. The acknowledgment gate is engaged.</summary>
    DeliveredAwaitingObservation,

    /// <summary>The agent has acknowledged the file as fully reviewed.</summary>
    ReviewedComplete,

    /// <summary>The agent has acknowledged the file as deliberately skipped (with a reason).</summary>
    SkippedWithReason,
}
