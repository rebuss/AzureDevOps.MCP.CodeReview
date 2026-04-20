using REBUSS.Pure.Core.Services.CopilotReview;
using REBUSS.Pure.Properties;

namespace REBUSS.Pure.Services.CopilotReview;

/// <summary>
/// Builds the user-facing error message thrown by the tool handlers when the
/// Copilot review layer is unavailable. Picks wording per <see cref="CopilotVerdict.Reason"/>
/// so the operator sees the actual failure (e.g. <c>StartFailure</c> meaning the CLI
/// subprocess could not launch) instead of a generic "re-authenticate" prompt.
/// </summary>
internal static class CopilotUnavailableMessage
{
    public static string Format(CopilotVerdict verdict)
    {
        // DisabledByConfig carries no remediation text (FR-016) — fall back to the
        // legacy enable-it message so the user gets actionable guidance.
        if (verdict.Reason == CopilotAuthReason.DisabledByConfig)
            return Resources.ErrorCopilotRequired;

        var remediation = string.IsNullOrWhiteSpace(verdict.Remediation)
            ? "See server logs for details."
            : verdict.Remediation;
        return string.Format(Resources.ErrorCopilotUnavailable, verdict.Reason, remediation);
    }
}
