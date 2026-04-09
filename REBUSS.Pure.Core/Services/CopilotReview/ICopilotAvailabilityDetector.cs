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
    /// process. Never throws; on failure returns <c>false</c>.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}
