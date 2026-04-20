namespace REBUSS.Pure.Core.Services.AgentInvocation;

/// <summary>
/// One-shot prompt → text abstraction used by review/validation code paths.
/// Hides whether the backend is GitHub Copilot SDK or Claude Code CLI.
/// </summary>
/// <remarks>
/// <para>
/// Contract notes:
/// <list type="bullet">
///   <item>Each call is independent — no session state is retained between invocations.</item>
///   <item>Streaming is hidden — if the backend streams, the implementation accumulates chunks
///         and returns the full assistant message as a single string.</item>
///   <item>Authentication is the implementation's concern — callers do not plumb tokens.</item>
///   <item>Failures throw. Cancellation propagates via <see cref="OperationCanceledException"/>.</item>
/// </list>
/// </para>
/// </remarks>
public interface IAgentInvoker
{
    /// <summary>
    /// Sends <paramref name="prompt"/> to the agent and returns the full response text.
    /// </summary>
    /// <param name="prompt">Complete prompt text — caller assembles templates, enriched content, etc.</param>
    /// <param name="model">
    /// Optional model hint. Copilot implementations use this to select an entitled model;
    /// Claude implementations ignore it (the CLI uses whichever model the session is logged into).
    /// </param>
    /// <param name="cancellationToken">Request cancellation.</param>
    /// <returns>Raw assistant response text. Never <c>null</c>, may be empty.</returns>
    Task<string> InvokeAsync(string prompt, string? model, CancellationToken cancellationToken);
}
