namespace REBUSS.Pure.AzureDevOps.Configuration;

/// <summary>
/// Persists and retrieves cached configuration data (tokens and account info)
/// in a local application data folder.
/// </summary>
public interface ILocalConfigStore
{
    /// <summary>
    /// Loads the previously cached configuration, or <c>null</c> if nothing is cached.
    /// </summary>
    CachedConfig? Load();

    /// <summary>
    /// Saves configuration data to local storage.
    /// </summary>
    void Save(CachedConfig config);
}

/// <summary>
/// Configuration data that is persisted locally between runs.
/// </summary>
public sealed class CachedConfig
{
    public string? OrganizationName { get; set; }
    public string? ProjectName { get; set; }
    public string? RepositoryName { get; set; }
    public string? AccessToken { get; set; }
    public string? TokenType { get; set; }
    public DateTime? TokenExpiresOn { get; set; }
}
