namespace REBUSS.Pure.GitHub.Configuration;

/// <summary>
/// Acquires a GitHub access token via the GitHub CLI (<c>gh auth token</c>).
/// </summary>
public interface IGitHubCliTokenProvider
{
    /// <summary>
    /// Attempts to get a GitHub access token from the GitHub CLI.
    /// Returns <c>null</c> if the CLI is not installed, the user is not logged in,
    /// or the command fails for any reason.
    /// </summary>
    Task<GitHubCliToken?> GetTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a token acquired from <c>gh auth token</c>.
/// GitHub CLI tokens do not carry an explicit expiry; <see cref="ExpiresOn"/> is set to
/// a far-future sentinel value indicating the token is valid until revoked.
/// </summary>
public sealed record GitHubCliToken(string AccessToken, DateTime ExpiresOn);
