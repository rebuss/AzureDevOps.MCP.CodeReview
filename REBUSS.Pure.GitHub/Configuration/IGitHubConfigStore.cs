namespace REBUSS.Pure.GitHub.Configuration;

/// <summary>
/// Persists and retrieves cached GitHub configuration data
/// in a local application data folder.
/// </summary>
public interface IGitHubConfigStore
{
    /// <summary>
    /// Loads the previously cached configuration, or <c>null</c> if nothing is cached.
    /// </summary>
    GitHubCachedConfig? Load();

    /// <summary>
    /// Saves configuration data to local storage.
    /// </summary>
    void Save(GitHubCachedConfig config);
}

/// <summary>
/// Configuration data that is persisted locally between runs.
/// </summary>
public sealed class GitHubCachedConfig
{
    public string? Owner { get; set; }
    public string? RepositoryName { get; set; }
}
