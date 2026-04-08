namespace REBUSS.Pure.Services.ReviewSession;

/// <summary>
/// In-memory store of active and completed review sessions (FR-018).
/// Singleton lifetime — sessions are added on <c>begin_pr_review</c> and never
/// removed except by process exit. State lives only in process memory.
/// </summary>
public interface IReviewSessionStore
{
    bool TryGet(string sessionId, out ReviewSession session);
    void Add(ReviewSession session);
    int Count { get; }
}
