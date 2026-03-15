using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace REBUSS.Pure.AzureDevOpsIntegration.Configuration;

/// <summary>
/// Authentication provider that implements a chain-of-responsibility pattern:
/// <list type="number">
///   <item>If the user explicitly provides a PAT in configuration, always use it.</item>
///   <item>If a cached token exists locally and is not expired, use it.</item>
///   <item>Otherwise, return a clear error instructing the user to configure a PAT.</item>
/// </list>
/// </summary>
public class ChainedAuthenticationProvider : IAuthenticationProvider
{
    private readonly IOptions<AzureDevOpsOptions> _options;
    private readonly ILocalConfigStore _configStore;
    private readonly ILogger<ChainedAuthenticationProvider> _logger;

    public ChainedAuthenticationProvider(
        IOptions<AzureDevOpsOptions> options,
        ILocalConfigStore configStore,
        ILogger<ChainedAuthenticationProvider> logger)
    {
        _options = options;
        _configStore = configStore;
        _logger = logger;
    }

    public Task<AuthenticationHeaderValue> GetAuthenticationAsync(CancellationToken cancellationToken = default)
    {
        // 1. Explicit PAT from config — highest priority
        if (!string.IsNullOrWhiteSpace(_options.Value.PersonalAccessToken))
        {
            _logger.LogInformation("Using Personal Access Token from configuration");
            var base64Pat = Convert.ToBase64String(
                Encoding.ASCII.GetBytes($":{_options.Value.PersonalAccessToken}"));
            return Task.FromResult(new AuthenticationHeaderValue("Basic", base64Pat));
        }

        // 2. Cached token
        var cached = _configStore.Load();
        if (cached?.AccessToken is not null && cached.TokenType is not null)
        {
            if (cached.TokenExpiresOn is null || cached.TokenExpiresOn > DateTime.UtcNow.AddMinutes(5))
            {
                _logger.LogInformation("Using cached {TokenType} token", cached.TokenType);
                return Task.FromResult(BuildHeaderFromCachedToken(cached));
            }

            _logger.LogDebug("Cached token expired");
        }

        // 3. No authentication available — instruct user to configure a PAT
        _logger.LogError("No authentication method available");
        throw new InvalidOperationException(BuildPatRequiredMessage());
    }

    private static AuthenticationHeaderValue BuildHeaderFromCachedToken(CachedConfig cached)
    {
        if (string.Equals(cached.TokenType, "Basic", StringComparison.OrdinalIgnoreCase))
            return new AuthenticationHeaderValue("Basic", cached.AccessToken);

        return new AuthenticationHeaderValue("Bearer", cached.AccessToken);
    }

    /// <summary>
    /// Builds a clear, actionable error message instructing the user to configure a PAT.
    /// </summary>
    internal static string BuildPatRequiredMessage()
    {
        return
            """
            ========================================
            AUTHENTICATION REQUIRED
            ========================================

            REBUSS.Pure requires a Personal Access Token (PAT) to access Azure DevOps.

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

            After saving the file, restart Visual Studio (or your MCP client).

            ========================================
            """;
    }
}
