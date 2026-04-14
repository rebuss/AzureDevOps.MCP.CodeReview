namespace REBUSS.Pure.Services.CopilotReview.Validation;

/// <summary>
/// A finding after the validation pass, carrying a verdict. Feature 021.
/// </summary>
public sealed record ValidatedFinding
{
    /// <summary>The original parsed finding.</summary>
    public required ParsedFinding Finding { get; init; }

    /// <summary>
    /// Verdict: <see cref="FindingVerdict.Valid"/> (keep), <see cref="FindingVerdict.FalsePositive"/>
    /// (remove), or <see cref="FindingVerdict.Uncertain"/> (tag with <c>[uncertain]</c>).
    /// </summary>
    public required FindingVerdict Verdict { get; init; }

    /// <summary>One-sentence explanation from the validation model. <c>null</c> if the finding skipped validation.</summary>
    public string? Reason { get; init; }
}
