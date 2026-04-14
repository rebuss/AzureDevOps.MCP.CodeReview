namespace REBUSS.Pure.Services.CopilotReview.Validation;

/// <summary>
/// A finding paired with the full source code of its enclosing method/scope, resolved by
/// <see cref="FindingScopeResolver"/>. When scope extraction fails, <see cref="ResolutionFailure"/>
/// indicates why — the validator uses this to decide whether to call Copilot or short-circuit
/// to a verdict. Feature 021.
/// </summary>
public sealed record FindingWithScope
{
    /// <summary>The original parsed finding.</summary>
    public required ParsedFinding Finding { get; init; }

    /// <summary>
    /// Full source code of the enclosing method, property, constructor, or class body.
    /// Empty string if scope was not resolved.
    /// </summary>
    public required string ScopeSource { get; init; }

    /// <summary>
    /// Formatted scope name from <c>ScopeResolver</c> (e.g., <c>"GitHubScmClientTests.ctor(...)"</c>).
    /// Empty string if scope was not resolved.
    /// </summary>
    public required string ScopeName { get; init; }

    /// <summary>
    /// <see cref="ScopeResolutionFailure.None"/> when scope was extracted; otherwise indicates why
    /// resolution failed. Drives the validator's verdict mapping for unresolved findings.
    /// </summary>
    public required ScopeResolutionFailure ResolutionFailure { get; init; }
}
