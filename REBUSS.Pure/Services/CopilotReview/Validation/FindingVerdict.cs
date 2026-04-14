namespace REBUSS.Pure.Services.CopilotReview.Validation;

/// <summary>
/// Verdict rendered by the finding validator for a single review finding.
/// Feature 021 (false-positive reduction).
/// </summary>
public enum FindingVerdict
{
    /// <summary>Issue genuinely exists in the source code.</summary>
    Valid,

    /// <summary>
    /// Issue does not exist; caused by diff context misinterpretation or
    /// incomplete code visibility. Filtered from final output.
    /// </summary>
    FalsePositive,

    /// <summary>
    /// Cannot determine from the provided scope alone. Tagged with
    /// <c>[uncertain]</c> prefix in final output — never silently dropped.
    /// </summary>
    Uncertain,
}
