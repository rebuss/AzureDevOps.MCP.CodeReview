namespace REBUSS.Pure.Core.Services.CopilotReview;

/// <summary>
/// Test seam over <c>CopilotClient.CreateSessionAsync</c>. The production implementation
/// wraps the returned <c>CopilotSession</c> in a <see cref="ICopilotSessionHandle"/>;
/// tests substitute a hand-rolled fake. Per research.md Decision 8.
/// <para>
/// <b>Thread-safety contract:</b> implementations MUST be safe to call concurrently from
/// multiple threads. The review pipeline dispatches pages in parallel batches
/// (<c>CopilotReviewOrchestrator.BackgroundBodyAsync</c>), so each batch fires several
/// <see cref="CreateSessionAsync"/> calls simultaneously. Fake implementations used in
/// tests should avoid shared mutable state, or guard it with synchronization — otherwise
/// they silently re-introduce a concurrency bug that production code does not have.
/// </para>
/// </summary>
public interface ICopilotSessionFactory
{
    /// <summary>
    /// Creates a new Copilot session configured for single-shot code review:
    /// one-time prompt, no streaming, no infinite sessions, <c>PermissionHandler.ApproveAll</c>.
    /// Throws if <see cref="ICopilotClientProvider"/> has not been started.
    /// <para>
    /// Safe to call concurrently — see the thread-safety contract on
    /// <see cref="ICopilotSessionFactory"/>.
    /// </para>
    /// </summary>
    Task<ICopilotSessionHandle> CreateSessionAsync(string model, CancellationToken ct);
}
