using System.Net.Http.Headers;

namespace REBUSS.Pure.AzureDevOpsIntegration.Configuration;

/// <summary>
/// Provides authentication credentials for Azure DevOps API calls.
/// </summary>
public interface IAuthenticationProvider
{
    /// <summary>
    /// Returns the authentication header value to use for Azure DevOps REST API requests.
    /// </summary>
    Task<AuthenticationHeaderValue> GetAuthenticationAsync(CancellationToken cancellationToken = default);
}
