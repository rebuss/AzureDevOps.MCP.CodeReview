using System.Net.Http.Headers;

namespace REBUSS.Pure.GitHub.Configuration;

/// <summary>
/// Provides authentication credentials for GitHub API calls.
/// </summary>
public interface IGitHubAuthenticationProvider
{
    /// <summary>
    /// Returns the authentication header value to use for GitHub REST API requests.
    /// </summary>
    Task<AuthenticationHeaderValue> GetAuthenticationAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the in-memory and on-disk cached token so the next call to
    /// <see cref="GetAuthenticationAsync"/> re-acquires a fresh one via GitHub CLI.
    /// Has no effect when a PAT is configured.
    /// </summary>
    void InvalidateCachedToken();
}
