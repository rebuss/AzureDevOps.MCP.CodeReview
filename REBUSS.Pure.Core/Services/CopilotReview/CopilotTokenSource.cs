namespace REBUSS.Pure.Core.Services.CopilotReview;

/// <summary>
/// Identifies which channel supplied the token (or absence thereof) used by the
/// Copilot SDK session. Logged exactly once per process on first verification.
/// The token VALUE itself is never stored, logged, or derived from this type.
/// Feature 018 (FR-013a).
/// </summary>
public enum CopilotTokenSource
{
    /// <summary>No source resolved yet. Test-sentinel value; never emitted in production.</summary>
    None = 0,

    /// <summary><c>REBUSS_COPILOT_TOKEN</c> environment variable (highest priority, FR-011).</summary>
    EnvironmentOverride,

    /// <summary><c>CopilotReview:GitHubToken</c> configuration key.</summary>
    ConfigOverride,

    /// <summary>Default SDK fallback: <c>UseLoggedInUser = true</c> — uses <c>gh auth login</c> session.</summary>
    LoggedInUser,
}

/// <summary>
/// Maps <see cref="CopilotTokenSource"/> values to the log-safe dashed string labels
/// used in <c>rebuss-pure init</c> banners and operator-facing documentation.
/// The enum name form (e.g. <c>LoggedInUser</c>) is used in structured-log fields;
/// the dashed form (e.g. <c>logged-in-user</c>) is used here. See
/// <c>data-model.md §3</c> for the rationale behind the two forms.
/// </summary>
public static class CopilotTokenSourceExtensions
{
    public static string ToLogLabel(this CopilotTokenSource source) => source switch
    {
        CopilotTokenSource.EnvironmentOverride => "env-override",
        CopilotTokenSource.ConfigOverride => "config-override",
        CopilotTokenSource.LoggedInUser => "logged-in-user",
        _ => "none",
    };
}
