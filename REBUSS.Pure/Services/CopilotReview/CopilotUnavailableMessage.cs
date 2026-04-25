using REBUSS.Pure.Core.Services.CopilotReview;
using REBUSS.Pure.Properties;

namespace REBUSS.Pure.Services.CopilotReview;

/// <summary>
/// Builds the user-facing error message thrown by the tool handlers when the
/// AI review layer is unavailable. Wording is agent-neutral — the underlying verdict
/// type still wears <c>Copilot</c> in its name for legacy reasons, but the message
/// the agent sees never mentions Copilot specifically; the verdict's own
/// <see cref="CopilotVerdict.Remediation"/> carries any provider-specific guidance.
/// </summary>
internal static class CopilotUnavailableMessage
{
    public static string Format(CopilotVerdict verdict)
    {
        // DisabledByConfig carries no remediation text (FR-016) — fall back to the
        // legacy enable-it message so the user gets actionable guidance.
        if (verdict.Reason == CopilotAuthReason.DisabledByConfig)
            return Resources.ErrorAgentReviewLayerRequired;

        var remediation = string.IsNullOrWhiteSpace(verdict.Remediation)
            ? "See server logs for details."
            : verdict.Remediation;
        return string.Format(Resources.ErrorAgentReviewLayerUnavailable, verdict.Reason, remediation);
    }
}
