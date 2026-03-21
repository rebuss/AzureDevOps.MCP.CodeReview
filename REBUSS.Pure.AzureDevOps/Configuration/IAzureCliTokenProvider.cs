namespace REBUSS.Pure.AzureDevOps.Configuration;

/// <summary>
/// Acquires an Azure DevOps access token via the Azure CLI (<c>az account get-access-token</c>).
/// </summary>
public interface IAzureCliTokenProvider
{
    /// <summary>
    /// Attempts to get an Azure DevOps access token from the Azure CLI.
    /// Returns <c>null</c> if the CLI is not installed, the user is not logged in,
    /// or the command fails for any reason.
    /// </summary>
    Task<AzureCliToken?> GetTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a token acquired from <c>az account get-access-token</c>.
/// </summary>
public sealed record AzureCliToken(string AccessToken, DateTime ExpiresOn);
