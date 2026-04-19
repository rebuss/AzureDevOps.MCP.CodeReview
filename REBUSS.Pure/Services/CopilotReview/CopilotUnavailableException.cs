using REBUSS.Pure.Core.Services.CopilotReview;

namespace REBUSS.Pure.Services.CopilotReview;

/// <summary>
/// Thrown by <c>CopilotAvailabilityDetector.IsAvailableAsync</c> in strict mode
/// (<c>CopilotReview:StrictMode = true</c>) when the cached verdict signals a real
/// verification failure. Caught by <c>GetPullRequestContentToolHandler</c> and
/// translated into an MCP tool-error envelope. Feature 018 (FR-015).
/// <para>
/// The <see cref="Message"/> is the verdict's remediation string — already
/// FR-013a-safe by construction: it never contains the token value.
/// </para>
/// </summary>
public sealed class CopilotUnavailableException : Exception
{
    public CopilotVerdict Verdict { get; }

    public CopilotUnavailableException(CopilotVerdict verdict)
        : base(verdict.Remediation)
    {
        Verdict = verdict;
    }
}
