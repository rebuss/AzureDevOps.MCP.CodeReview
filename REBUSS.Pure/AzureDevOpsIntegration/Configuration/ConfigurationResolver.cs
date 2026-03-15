using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using REBUSS.Pure.Mcp;

namespace REBUSS.Pure.AzureDevOpsIntegration.Configuration;

/// <summary>
/// Post-configures <see cref="AzureDevOpsOptions"/> by merging values from:
/// <list type="number">
///   <item>Explicit user configuration (appsettings, environment variables) — already bound.</item>
///   <item>Locally cached configuration.</item>
///   <item>Auto-detected Git remote information (using workspace root from MCP roots or localRepoPath).</item>
/// </list>
/// Explicit user values always take precedence. After resolution, the merged
/// repository information is cached locally for future runs.
/// Runs automatically during the first <c>IOptions&lt;AzureDevOpsOptions&gt;.Value</c> access.
/// </summary>
public class ConfigurationResolver : IPostConfigureOptions<AzureDevOpsOptions>
{
    private readonly IGitRemoteDetector _gitRemoteDetector;
    private readonly ILocalConfigStore _configStore;
    private readonly IWorkspaceRootProvider _workspaceRootProvider;
    private readonly ILogger<ConfigurationResolver> _logger;

    public ConfigurationResolver(
        IGitRemoteDetector gitRemoteDetector,
        ILocalConfigStore configStore,
        IWorkspaceRootProvider workspaceRootProvider,
        ILogger<ConfigurationResolver> logger)
    {
        _gitRemoteDetector = gitRemoteDetector;
        _configStore = configStore;
        _workspaceRootProvider = workspaceRootProvider;
        _logger = logger;
    }

    public void PostConfigure(string? name, AzureDevOpsOptions options)
    {
        var cached = _configStore.Load();
        var detected = DetectGitInfo(options);

        options.OrganizationName = Resolve(
            options.OrganizationName,
            cached?.OrganizationName,
            detected?.OrganizationName,
            nameof(AzureDevOpsOptions.OrganizationName));

        options.ProjectName = Resolve(
            options.ProjectName,
            cached?.ProjectName,
            detected?.ProjectName,
            nameof(AzureDevOpsOptions.ProjectName));

        options.RepositoryName = Resolve(
            options.RepositoryName,
            cached?.RepositoryName,
            detected?.RepositoryName,
            nameof(AzureDevOpsOptions.RepositoryName));

        _logger.LogInformation(
            "Resolved configuration: org={Organization}, project={Project}, repo={Repository}, pat={HasPat}",
            options.OrganizationName, options.ProjectName, options.RepositoryName,
            !string.IsNullOrWhiteSpace(options.PersonalAccessToken) ? "provided" : "not provided");

        if (string.IsNullOrWhiteSpace(options.OrganizationName) ||
            string.IsNullOrWhiteSpace(options.ProjectName) ||
            string.IsNullOrWhiteSpace(options.RepositoryName))
        {
            _logger.LogWarning(
                "Configuration is incomplete — some fields could not be resolved. " +
                "Tools will return errors until configuration is provided via MCP roots, " +
                "appsettings, environment variables, or AzureDevOps:LocalRepoPath.");
            return;
        }

        // Cache the resolved repository info (not the token — that's handled by auth provider)
        var configToCache = cached ?? new CachedConfig();
        configToCache.OrganizationName = options.OrganizationName;
        configToCache.ProjectName = options.ProjectName;
        configToCache.RepositoryName = options.RepositoryName;
        _configStore.Save(configToCache);
    }

    private DetectedGitInfo? DetectGitInfo(AzureDevOpsOptions options)
    {
        // 1. Try workspace root from MCP roots or localRepoPath
        var repoRoot = _workspaceRootProvider.ResolveRepositoryRoot();
        if (repoRoot is not null)
        {
            _logger.LogDebug("Using workspace repository root for git detection: {RepoRoot}", repoRoot);
            var detected = _gitRemoteDetector.Detect(repoRoot);
            if (detected is not null)
                return detected;
        }

        // 2. Fall back to default detection (cwd + executable location)
        _logger.LogDebug("Falling back to default git remote detection");
        return _gitRemoteDetector.Detect();
    }

    internal static string Resolve(string? userValue, string? cachedValue, string? detectedValue, string fieldName)
    {
        if (!string.IsNullOrWhiteSpace(userValue))
            return userValue;

        if (!string.IsNullOrWhiteSpace(cachedValue))
            return cachedValue;

        if (!string.IsNullOrWhiteSpace(detectedValue))
            return detectedValue;

        return string.Empty;
    }
}
