using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace REBUSS.Pure.AzureDevOps.Configuration;

/// <summary>
/// Authentication provider that implements a chain-of-responsibility pattern:
/// <list type="number">
///   <item>If the user explicitly provides a PAT in configuration, always use it.</item>
///   <item>If a cached token exists locally and is not expired, use it.</item>
///   <item>If the Azure CLI is available and the user is logged in, acquire a token via <c>az account get-access-token</c> and cache it.</item>
///   <item>Otherwise, return a clear error instructing the user to run <c>az login</c> or configure a PAT.</item>
/// </list>
/// </summary>
public class ChainedAuthenticationProvider : IAuthenticationProvider
{
    private readonly IOptions<AzureDevOpsOptions> _options;
    private readonly ILocalConfigStore _configStore;
    private readonly IAzureCliTokenProvider _azureCliTokenProvider;
    private readonly ILogger<ChainedAuthenticationProvider> _logger;

    public ChainedAuthenticationProvider(
        IOptions<AzureDevOpsOptions> options,
        ILocalConfigStore configStore,
        IAzureCliTokenProvider azureCliTokenProvider,
        ILogger<ChainedAuthenticationProvider> logger)
    {
        _options = options;
        _configStore = configStore;
        _azureCliTokenProvider = azureCliTokenProvider;
        _logger = logger;
    }

    public async Task<AuthenticationHeaderValue> GetAuthenticationAsync(CancellationToken cancellationToken = default)
    {
        // 1. Explicit PAT from config — highest priority
        if (!string.IsNullOrWhiteSpace(_options.Value.PersonalAccessToken))
        {
            _logger.LogInformation("Using Personal Access Token from configuration");
            var base64Pat = Convert.ToBase64String(
                Encoding.ASCII.GetBytes($":{_options.Value.PersonalAccessToken}"));
            return new AuthenticationHeaderValue("Basic", base64Pat);
        }

        // 2. Cached token (not expired)
        // Basic tokens (cached PATs) have no CLI-managed expiry — treat null expiry as valid.
        // Bearer tokens (Azure CLI) must have an explicit expiry; null expiry means the token
        // was cached before expiry tracking was introduced and should be refreshed.
        var cached = _configStore.Load();
        if (cached?.AccessToken is not null && cached.TokenType is not null)
        {
            var isBasic = string.Equals(cached.TokenType, "Basic", StringComparison.OrdinalIgnoreCase);
            var notExpired = cached.TokenExpiresOn.HasValue && cached.TokenExpiresOn > DateTime.UtcNow.AddMinutes(5);

            if (isBasic || notExpired)
            {
                _logger.LogInformation("Using cached {TokenType} token", cached.TokenType);
                return BuildHeaderFromCachedToken(cached);
            }

            _logger.LogDebug("Cached Bearer token expired or has no expiry, attempting Azure CLI refresh");
        }

        // 3. Azure CLI — az account get-access-token
        var cliToken = await _azureCliTokenProvider.GetTokenAsync(cancellationToken);
        if (cliToken is not null)
        {
            _logger.LogInformation("Acquired Azure DevOps token via Azure CLI");
            CacheCliToken(cliToken);
            return new AuthenticationHeaderValue("Bearer", cliToken.AccessToken);
        }

        // 4. No authentication available — instruct user to run az login or configure a PAT
        _logger.LogError("No authentication method available");
        throw new InvalidOperationException(BuildAuthRequiredMessage());
    }

    public void InvalidateCachedToken()
    {
        try
        {
            var existing = _configStore.Load();
            if (existing is null) return;

            existing.AccessToken = null;
            existing.TokenType = null;
            existing.TokenExpiresOn = null;
            _configStore.Save(existing);

            _logger.LogDebug("Cached token invalidated");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to invalidate cached token");
        }
    }

    private void CacheCliToken(AzureCliToken token)
    {
        try
        {
            var existing = _configStore.Load() ?? new CachedConfig();
            existing.AccessToken = token.AccessToken;
            existing.TokenType = "Bearer";
            existing.TokenExpiresOn = token.ExpiresOn;
            _configStore.Save(existing);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cache Azure CLI token");
        }
    }

    private static AuthenticationHeaderValue BuildHeaderFromCachedToken(CachedConfig cached)
    {
        if (string.Equals(cached.TokenType, "Basic", StringComparison.OrdinalIgnoreCase))
            return new AuthenticationHeaderValue("Basic", cached.AccessToken);

        return new AuthenticationHeaderValue("Bearer", cached.AccessToken);
    }

    /// <summary>
    /// Builds a clear, actionable error message instructing the user to authenticate.
    /// </summary>
    internal static string BuildAuthRequiredMessage()
    {
        return
            """
            ========================================
            AUTHENTICATION REQUIRED
            ========================================

            REBUSS.Pure requires authentication to access Azure DevOps.

            OPTION 1 — Azure CLI (recommended, no PAT needed):

              Run the following command to log in:

                az login

              The server will automatically acquire a token for Azure DevOps.
              You can also run 'rebuss-pure init' (without --pat) to log in
              during initialization.

              If Azure CLI is not installed:
                https://learn.microsoft.com/en-us/cli/azure/install-azure-cli

            OPTION 2 — Personal Access Token (PAT):

              Create a file named 'appsettings.Local.json' next to the server executable
              (this file is excluded from Git via .gitignore):

                {
                  "AzureDevOps": {
                    "PersonalAccessToken": "<your-pat-here>"
                  }
                }

              To create a PAT:
                1. Go to https://dev.azure.com/<your-org>/_usersSettings/tokens
                2. Click '+ New Token'
                3. Select scope: Code (Read)
                4. Copy the generated token into the file above

            After authenticating, restart Visual Studio (or your MCP client).

            ========================================
            """;
    }
}
