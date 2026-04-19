namespace REBUSS.Pure.AzureDevOps.Configuration;

/// <summary>
/// Tunes how <see cref="REBUSS.Pure.AzureDevOps.Providers.AzureDevOpsDiffProvider"/> fetches
/// per-file content. Bound from the <c>AzureDevOps:Diff</c> section of <c>appsettings.json</c>.
/// </summary>
public sealed class AzureDevOpsDiffOptions
{
    public const string SectionName = "AzureDevOps:Diff";

    /// <summary>
    /// When the number of changed files in a PR exceeds this value, the diff provider
    /// downloads the base + target repository ZIPs once each and reads file contents
    /// from disk, instead of issuing two <c>items</c> API requests per file (which can
    /// exceed Azure DevOps rate limits on large PRs).
    /// <para>
    /// Set to <c>0</c> to disable the ZIP path entirely (always fetch per file). The
    /// default of 30 keeps small PRs on the cheap per-file path while protecting
    /// large refactor PRs from rate-limit throttling.
    /// </para>
    /// </summary>
    public int ZipFallbackThreshold { get; set; } = 30;
}
