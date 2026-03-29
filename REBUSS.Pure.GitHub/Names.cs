namespace REBUSS.Pure.GitHub;

/// <summary>
/// Canonical GitHub provider identifier strings used for provider selection,
/// configuration section keys, and case-insensitive normalization.
/// </summary>
public static class Names
{
    /// <summary>Canonical GitHub provider identifier (proper case).</summary>
    public const string Provider = "GitHub";

    /// <summary>Lowercase GitHub provider identifier, used for case-insensitive normalization.</summary>
    public const string ProviderLower = "github";

    /// <summary>GitHub domain used for git remote detection and URL construction.</summary>
    public const string Domain = "github.com";
}
