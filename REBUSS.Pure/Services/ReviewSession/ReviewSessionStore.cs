using System.Collections.Concurrent;

namespace REBUSS.Pure.Services.ReviewSession;

/// <summary>
/// Singleton in-memory store backed by <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// This is the load-bearing exception to constitution Principle VI documented in
/// <c>specs/012-review-session-mvp/spec.md</c> FR-030.
/// </summary>
internal sealed class ReviewSessionStore : IReviewSessionStore
{
    private readonly ConcurrentDictionary<string, ReviewSession> _sessions = new(StringComparer.Ordinal);

    public bool TryGet(string sessionId, out ReviewSession session)
    {
        if (sessionId is null)
        {
            session = null!;
            return false;
        }
        return _sessions.TryGetValue(sessionId, out session!);
    }

    public void Add(ReviewSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!_sessions.TryAdd(session.SessionId, session))
            throw new InvalidOperationException($"Session id '{session.SessionId}' already exists in store.");
    }

    public int Count => _sessions.Count;
}
