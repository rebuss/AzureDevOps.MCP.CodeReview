namespace REBUSS.Pure.Services.CopilotReview.Validation;

/// <summary>
/// Reason that the scope of a finding could not be resolved — drives the validator's
/// pre-filter mapping (spec US3.1 / US3.2). Feature 021.
/// </summary>
public enum ScopeResolutionFailure
{
    /// <summary>Scope was successfully extracted and the finding will be validated by Copilot.</summary>
    None,

    /// <summary>
    /// Finding's file path is not a <c>.cs</c> file — Roslyn analysis is not applicable.
    /// Maps to <c>Verdict = Valid</c> (passthrough unfiltered per spec US3.1).
    /// </summary>
    NotCSharp,

    /// <summary>
    /// File is C# but <c>DiffSourceResolver</c> could not resolve its source
    /// (download timeout, file missing from archive, >100KB). Maps to
    /// <c>Verdict = Uncertain</c> (tagged per spec US3.2).
    /// </summary>
    SourceUnavailable,

    /// <summary>
    /// File source is available but no enclosing member could be mapped to the
    /// finding's line (e.g., top-level statement, class-level field outside any
    /// method). Maps to <c>Verdict = Uncertain</c> (tagged per spec US3.2).
    /// </summary>
    ScopeNotFound,
}
