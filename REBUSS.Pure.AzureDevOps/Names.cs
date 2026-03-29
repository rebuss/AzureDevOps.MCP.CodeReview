namespace REBUSS.Pure.AzureDevOps;

/// <summary>
/// Canonical Azure DevOps provider identifier strings used for provider selection,
/// configuration section keys, and case-insensitive normalization.
/// </summary>
public static class Names
{
    /// <summary>Canonical Azure DevOps provider identifier (proper case).</summary>
    public const string Provider = "AzureDevOps";

    /// <summary>Lowercase Azure DevOps provider identifier, used for case-insensitive normalization.</summary>
    public const string ProviderLower = "azuredevops";

    /// <summary>Azure DevOps domain used for git remote detection and URL construction.</summary>
    public const string Domain = "dev.azure.com";

    /// <summary>Legacy Azure DevOps domain (visualstudio.com), used for git remote detection.</summary>
    public const string LegacyDomain = "visualstudio.com";
}
