namespace REBUSS.Pure.Core.Services.CopilotReview;

/// <summary>
/// Test seam over <c>CopilotClient.CreateSessionAsync</c>. The production implementation
/// wraps the returned <c>CopilotSession</c> in a <see cref="ICopilotSessionHandle"/>;
/// tests substitute a hand-rolled fake. Per research.md Decision 8.
/// </summary>
public interface ICopilotSessionFactory
{
    /// <summary>
    /// Creates a new Copilot session configured for single-shot code review:
    /// one-time prompt, no streaming, no infinite sessions, <c>PermissionHandler.ApproveAll</c>.
    /// Throws if <see cref="ICopilotClientProvider"/> has not been started.
    /// </summary>
    Task<ICopilotSessionHandle> CreateSessionAsync(string model, CancellationToken ct);
}
