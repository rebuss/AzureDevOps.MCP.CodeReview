using System.Net.Http.Headers;

namespace REBUSS.Pure.AzureDevOps.Configuration;

/// <summary>
/// Provides authentication credentials for Azure DevOps API calls.
/// </summary>
public interface IAuthenticationProvider
{
    /// <summary>
    /// Returns the authentication header value to use for Azure DevOps REST API requests.
    /// </summary>
    Task<AuthenticationHeaderValue> GetAuthenticationAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the in-memory and on-disk cached token so the next call to
    /// <see cref="GetAuthenticationAsync"/> re-acquires a fresh one via Azure CLI.
    /// Has no effect when a PAT is configured.
    /// </summary>
    void InvalidateCachedToken();
}
