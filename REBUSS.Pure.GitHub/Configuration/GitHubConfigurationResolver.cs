using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using REBUSS.Pure.Core;

namespace REBUSS.Pure.GitHub.Configuration;

/// <summary>
/// Post-configures <see cref="GitHubOptions"/> by merging values from:
/// <list type="number">
///   <item>Explicit user configuration (appsettings, environment variables) — already bound.</item>
///   <item>Auto-detected Git remote information (using workspace root from MCP roots or localRepoPath).</item>
///   <item>Locally cached configuration (fallback when detection is unavailable).</item>
/// </list>
/// Explicit user values always take precedence. Auto-detected values from the
/// current workspace take priority over cached values to prevent stale config
/// from overriding the actual repository. After resolution, the merged
/// repository information is cached locally for future runs.
/// Runs automatically during the first <c>IOptions&lt;GitHubOptions&gt;.Value</c> access.
/// </summary>
public class GitHubConfigurationResolver : IPostConfigureOptions<GitHubOptions>
{
    private readonly IGitHubRemoteDetector _gitRemoteDetector;
    private readonly IGitHubConfigStore _configStore;
    private readonly IWorkspaceRootProvider _workspaceRootProvider;
    private readonly ILogger<GitHubConfigurationResolver> _logger;

    public GitHubConfigurationResolver(
        IGitHubRemoteDetector gitRemoteDetector,
        IGitHubConfigStore configStore,
        IWorkspaceRootProvider workspaceRootProvider,
        ILogger<GitHubConfigurationResolver> logger)
    {
        _gitRemoteDetector = gitRemoteDetector;
        _configStore = configStore;
        _workspaceRootProvider = workspaceRootProvider;
        _logger = logger;
    }

    public void PostConfigure(string? name, GitHubOptions options)
    {
        var cached = _configStore.Load();
        var detected = DetectGitInfo(options);

        options.Owner = Resolve(
            options.Owner,
            cached?.Owner,
            detected?.Owner,
            nameof(GitHubOptions.Owner));

        options.RepositoryName = Resolve(
            options.RepositoryName,
            cached?.RepositoryName,
            detected?.RepositoryName,
            nameof(GitHubOptions.RepositoryName));

        _logger.LogInformation(
            "Resolved GitHub configuration: owner={Owner}, repo={Repository}, pat={HasPat}",
            options.Owner, options.RepositoryName,
            !string.IsNullOrWhiteSpace(options.PersonalAccessToken) ? "provided" : "not provided");

        if (string.IsNullOrWhiteSpace(options.Owner) ||
            string.IsNullOrWhiteSpace(options.RepositoryName))
        {
            _logger.LogWarning(
                "GitHub configuration is incomplete — some fields could not be resolved. " +
                "Tools will return errors until configuration is provided via MCP roots, " +
                "appsettings, environment variables, or GitHub:LocalRepoPath.");
            return;
        }

        var configToCache = cached ?? new GitHubCachedConfig();
        configToCache.Owner = options.Owner;
        configToCache.RepositoryName = options.RepositoryName;
        _configStore.Save(configToCache);
    }

    private DetectedGitHubInfo? DetectGitInfo(GitHubOptions options)
    {
        var repoRoot = _workspaceRootProvider.ResolveRepositoryRoot();
        if (repoRoot is not null)
        {
            _logger.LogDebug("Using workspace repository root for git detection: {RepoRoot}", repoRoot);
            var detected = _gitRemoteDetector.Detect(repoRoot);
            if (detected is not null)
                return detected;
        }

        _logger.LogDebug("Falling back to default git remote detection");
        return _gitRemoteDetector.Detect();
    }

    internal static string Resolve(string? userValue, string? cachedValue, string? detectedValue, string fieldName)
    {
        if (!string.IsNullOrWhiteSpace(userValue))
            return userValue;

        if (!string.IsNullOrWhiteSpace(detectedValue))
            return detectedValue;

        if (!string.IsNullOrWhiteSpace(cachedValue))
            return cachedValue;

        return string.Empty;
    }
}
