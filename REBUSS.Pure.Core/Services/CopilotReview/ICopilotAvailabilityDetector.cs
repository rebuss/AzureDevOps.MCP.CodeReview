namespace REBUSS.Pure.Core.Services.CopilotReview;

/// <summary>
/// Runtime probe for "can the MCP server perform a server-side Copilot review right now?".
/// Implementations MUST cache the answer for the lifetime of the process (research.md
/// Decision 6) so the probe cost amortizes to zero after the first call.
/// </summary>
public interface ICopilotAvailabilityDetector
{
    /// <summary>
    /// Returns <c>true</c> iff Copilot-assisted review is enabled by operator configuration
    /// AND the underlying client/CLI has been successfully started at least once in this
    /// process.
    /// <para>
    /// <b>Graceful mode (default)</b>: never throws — returns <c>false</c> on any
    /// verification failure and the orchestrator falls back to the content-only path.
    /// </para>
    /// <para>
    /// <b>Strict mode</b> (<c>CopilotReview:StrictMode = true</c>, feature 018): throws
    /// <c>CopilotUnavailableException</c> when the cached verdict has
    /// <see cref="CopilotVerdict.IsAvailable"/> <c>= false</c> AND the reason is a real
    /// verification failure (NOT <see cref="CopilotAuthReason.DisabledByConfig"/> —
    /// see FR-016). Cancellation still propagates as <c>OperationCanceledException</c>.
    /// </para>
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the cached <see cref="CopilotVerdict"/> without ever throwing for
    /// strict-mode reasons. Diagnostic callers (e.g. <c>rebuss-pure init</c>'s Copilot
    /// setup step) use this to read the same verdict that <see cref="IsAvailableAsync"/>
    /// would gate on, without being subject to the strict-mode throw. Cancellation
    /// still propagates. Feature 018 (FR-017).
    /// </summary>
    Task<CopilotVerdict> GetVerdictAsync(CancellationToken ct = default);
}
