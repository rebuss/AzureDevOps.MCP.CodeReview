namespace REBUSS.Pure.Core.Services.CopilotReview;

/// <summary>
/// Opaque handle to an in-flight Copilot review session. Abstraction exists so
/// <c>CopilotPageReviewer</c> can be unit-tested against a fake session without
/// needing to mock the concrete <c>CopilotSession</c> class from the SDK (which has
/// non-virtual methods). Per research.md Decision 8.
/// </summary>
public interface ICopilotSessionHandle : IAsyncDisposable
{
    /// <summary>
    /// Sends a single prompt to the session. Returns the message id. Response is
    /// delivered asynchronously via the handler registered through <see cref="On"/>.
    /// </summary>
    Task<string> SendAsync(string prompt, CancellationToken ct);

    /// <summary>
    /// Subscribes to the session's event stream. Events are delivered as <see cref="object"/>
    /// so this interface does not leak SDK types; subscribers in <c>REBUSS.Pure</c>
    /// pattern-match on concrete SDK event types (<c>AssistantMessageEvent</c>,
    /// <c>SessionIdleEvent</c>, <c>SessionErrorEvent</c>, …).
    /// </summary>
    /// <returns>A disposable that unsubscribes when disposed.</returns>
    IDisposable On(Action<object> handler);
}
