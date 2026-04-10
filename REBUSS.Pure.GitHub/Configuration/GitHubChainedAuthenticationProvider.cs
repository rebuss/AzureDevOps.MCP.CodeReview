using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using REBUSS.Pure.GitHub.Properties;

namespace REBUSS.Pure.GitHub.Configuration;

/// <summary>
/// Authentication provider that implements a chain-of-responsibility pattern:
/// <list type="number">
///   <item>If the user explicitly provides a PAT in configuration, always use it.</item>
///   <item>If a cached token exists locally and is not expired, use it.</item>
///   <item>If the GitHub CLI is available and the user is logged in, acquire a token via <c>gh auth token</c> and cache it.</item>
///   <item>Otherwise, return a clear error instructing the user to run <c>gh auth login</c> or configure a PAT.</item>
/// </list>
/// </summary>
public class GitHubChainedAuthenticationProvider : IGitHubAuthenticationProvider
{
    private readonly IOptions<GitHubOptions> _options;
    private readonly IGitHubConfigStore _configStore;
    private readonly IGitHubCliTokenProvider _ghCliTokenProvider;
    private readonly ILogger<GitHubChainedAuthenticationProvider> _logger;

    public GitHubChainedAuthenticationProvider(
        IOptions<GitHubOptions> options,
        IGitHubConfigStore configStore,
        IGitHubCliTokenProvider ghCliTokenProvider,
        ILogger<GitHubChainedAuthenticationProvider> logger)
    {
        _options = options;
        _configStore = configStore;
        _ghCliTokenProvider = ghCliTokenProvider;
        _logger = logger;
    }

    public async Task<AuthenticationHeaderValue> GetAuthenticationAsync(CancellationToken cancellationToken = default)
    {
        // 1. Explicit PAT from config — highest priority
        if (!string.IsNullOrWhiteSpace(_options.Value.PersonalAccessToken))
        {
            _logger.LogInformation("Using GitHub Personal Access Token from configuration");
            return new AuthenticationHeaderValue("Bearer", _options.Value.PersonalAccessToken);
        }

        // 2. Cached token (not expired)
        var cached = _configStore.Load();
        if (cached?.AccessToken is not null)
        {
            var notExpired = !cached.TokenExpiresOn.HasValue || cached.TokenExpiresOn > DateTime.UtcNow.AddMinutes(5);
            if (notExpired)
            {
                _logger.LogDebug("Using cached GitHub token");
                return new AuthenticationHeaderValue("Bearer", cached.AccessToken);
            }

            _logger.LogDebug("Cached GitHub token expired, attempting GitHub CLI refresh");
        }

        // 3. GitHub CLI — gh auth token
        var cliToken = await _ghCliTokenProvider.GetTokenAsync(cancellationToken);
        if (cliToken is not null)
        {
            _logger.LogInformation("Acquired GitHub token via GitHub CLI");
            CacheCliToken(cliToken);
            return new AuthenticationHeaderValue("Bearer", cliToken.AccessToken);
        }

        // 4. No authentication available — instruct user to run gh auth login or configure a PAT
        _logger.LogError("No GitHub authentication method available");
        throw new InvalidOperationException(Resources.ErrorGitHubAuthRequired);
    }

    public void InvalidateCachedToken()
    {
        try
        {
            var existing = _configStore.Load();
            if (existing is null) return;

            existing.AccessToken = null;
            existing.TokenExpiresOn = null;
            _configStore.Save(existing);

            _logger.LogDebug("Cached GitHub token invalidated");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to invalidate cached GitHub token");
        }
    }

    private void CacheCliToken(GitHubCliToken token)
    {
        try
        {
            var existing = _configStore.Load() ?? new GitHubCachedConfig();
            existing.AccessToken = token.AccessToken;
            existing.TokenExpiresOn = token.ExpiresOn;
            _configStore.Save(existing);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cache GitHub CLI token");
        }
    }

    }
