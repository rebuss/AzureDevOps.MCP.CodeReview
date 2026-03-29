using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using REBUSS.Pure.AzureDevOps;
using REBUSS.Pure.Core;
using REBUSS.Pure.Properties;

namespace REBUSS.Pure.Services
{
    /// <summary>
    /// Resolves the workspace repository root path using (in priority order):
    /// <list type="number">
    ///   <item>CLI <c>--repo</c> argument (highest priority).</item>
    ///   <item>MCP roots fetched lazily from the SDK's <see cref="IMcpServer"/> via <c>RequestRootsAsync</c>.</item>
    ///   <item><c>LocalRepoPath</c> configuration fallback.</item>
    /// </list>
    /// Resolution is lazy — it only happens when <see cref="ResolveRepositoryRoot"/> is called.
    /// MCP roots are fetched once on first access and cached for subsequent calls.
    /// Reads <c>LocalRepoPath</c> directly from <see cref="IConfiguration"/> to avoid a
    /// circular dependency with <c>IPostConfigureOptions&lt;AzureDevOpsOptions&gt;</c>.
    /// </summary>
    public class McpWorkspaceRootProvider : IWorkspaceRootProvider
    {
        private readonly IConfiguration _configuration;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<McpWorkspaceRootProvider> _logger;
        private List<string> _rootUris = new();
        private string? _cliRepositoryPath;
        private bool _rootsFetched;
        private readonly object _lock = new();

        public McpWorkspaceRootProvider(
            IConfiguration configuration,
            IServiceProvider serviceProvider,
            ILogger<McpWorkspaceRootProvider> logger)
        {
            _configuration = configuration;
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public void SetCliRepositoryPath(string path)
        {
            if (IsUnexpandedVariable(path))
            {
                _logger.LogWarning(Resources.LogMcpWorkspaceRootProviderUnexpandedVariable, path);
                return;
            }

            lock (_lock)
            {
                _cliRepositoryPath = path;
            }

            _logger.LogDebug("CLI repository path set: {CliRepoPath}", path);
        }

        /// <summary>
        /// Returns <c>true</c> when <paramref name="path"/> still contains an unexpanded
        /// placeholder token such as <c>${workspaceFolder}</c> or <c>$(SolutionDir)</c>.
        /// These are client-specific variables that some clients (e.g. VS Code) expand
        /// before launching the server, but others (e.g. Visual Studio) pass through literally.
        /// </summary>
        internal static bool IsUnexpandedVariable(string path) =>
            path.Contains("${") || path.Contains("$(");

        public void SetRoots(IReadOnlyList<string> rootUris)
        {
            lock (_lock)
            {
                _rootUris = rootUris.ToList();
                _rootsFetched = true;
            }

            _logger.LogDebug("MCP roots received: {RootCount} root(s)", rootUris.Count);
            foreach (var uri in rootUris)
            {
                _logger.LogDebug("  Root: {RootUri}", uri);
            }
        }

        public IReadOnlyList<string> GetRootUris()
        {
            EnsureRootsFetched();

            lock (_lock)
            {
                return _rootUris.AsReadOnly();
            }
        }

        public string? ResolveRepositoryRoot()
        {
            // 1. Try CLI --repo argument (highest priority)
            string? cliPath;
            lock (_lock)
            {
                cliPath = _cliRepositoryPath;
            }

            if (!string.IsNullOrWhiteSpace(cliPath))
            {
                if (!Directory.Exists(cliPath))
                {
                    _logger.LogDebug("CLI --repo path does not exist: {Path}", cliPath);
                }
                else
                {
                    var repoRoot = FindGitRepositoryRoot(cliPath);
                    if (repoRoot is not null)
                    {
                        _logger.LogDebug("Resolved repository root from CLI --repo: {RepoRoot}", repoRoot);
                        return repoRoot;
                    }

                    _logger.LogDebug("CLI --repo path is not inside a Git repository: {Path}", cliPath);
                }
            }

            // 2. Try MCP roots (lazily fetched from SDK on first access)
            EnsureRootsFetched();

            List<string> rootUris;
            lock (_lock)
            {
                rootUris = _rootUris.ToList();
            }

            foreach (var uri in rootUris)
            {
                var localPath = ConvertUriToLocalPath(uri);
                if (localPath is null)
                {
                    _logger.LogDebug("Skipping non-file root URI: {Uri}", uri);
                    continue;
                }

                if (!Directory.Exists(localPath))
                {
                    _logger.LogDebug("Skipping MCP root (directory does not exist): {Path}", localPath);
                    continue;
                }

                var repoRoot = FindGitRepositoryRoot(localPath);
                if (repoRoot is not null)
                {
                    _logger.LogDebug("Resolved repository root from MCP root: {RepoRoot}", repoRoot);
                    return repoRoot;
                }

                _logger.LogDebug("MCP root is not inside a Git repository: {Path}", localPath);
            }

            // 3. Try localRepoPath fallback (read directly from IConfiguration to avoid circular dependency)
            var localRepoPath = _configuration.GetSection(Names.Provider)["LocalRepoPath"];
            if (!string.IsNullOrWhiteSpace(localRepoPath))
            {
                if (!Directory.Exists(localRepoPath))
                {
                    _logger.LogDebug("Configured localRepoPath does not exist: {Path}", localRepoPath);
                    return null;
                }

                var repoRoot = FindGitRepositoryRoot(localRepoPath);
                if (repoRoot is not null)
                {
                    _logger.LogDebug("Resolved repository root from localRepoPath: {RepoRoot}", repoRoot);
                    return repoRoot;
                }

                _logger.LogDebug("Configured localRepoPath is not inside a Git repository: {Path}", localRepoPath);
            }

            _logger.LogDebug("Repository root resolution failed: no CLI --repo, MCP roots, or localRepoPath available");
            return null;
        }

        /// <summary>
        /// Lazily fetches MCP roots from the SDK's <see cref="IMcpServer"/> on first access.
        /// In CLI mode (no MCP server), this is a no-op.
        /// </summary>
        private void EnsureRootsFetched()
        {
            lock (_lock)
            {
                if (_rootsFetched)
                    return;
                _rootsFetched = true;
            }

            try
            {
                var mcpServer = _serviceProvider.GetService<McpServer>();
                if (mcpServer is null)
                {
                    _logger.LogDebug("No IMcpServer available (CLI mode) — skipping MCP root resolution");
                    return;
                }

                // RequestRootsAsync asks the client for its workspace roots.
                // We block here because ResolveRepositoryRoot is synchronous and called
                // from IPostConfigureOptions which is also synchronous. Console apps have
                // no SynchronizationContext, so this is safe from deadlocks.
                var result = Task.Run(async () => await mcpServer.RequestRootsAsync(new ModelContextProtocol.Protocol.ListRootsRequestParams())).GetAwaiter().GetResult();

                var uris = result.Roots
                    .Select(r => r.Uri.ToString())
                    .ToList();

                lock (_lock)
                {
                    _rootUris = uris;
                }

                _logger.LogDebug("MCP roots fetched via SDK: {RootCount} root(s)", uris.Count);
                foreach (var uri in uris)
                {
                    _logger.LogDebug("  Root: {RootUri}", uri);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not fetch MCP roots from SDK (client may not support roots/list)");
            }
        }

        internal static string? ConvertUriToLocalPath(string uri)
        {
            if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) &&
                string.Equals(parsed.Scheme, "file", StringComparison.OrdinalIgnoreCase))
            {
                return parsed.LocalPath;
            }

            return null;
        }

        internal static string? FindGitRepositoryRoot(string? startDirectory)
        {
            if (string.IsNullOrWhiteSpace(startDirectory))
                return null;

            var dir = new DirectoryInfo(startDirectory);

            while (dir is not null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                    return dir.FullName;

                dir = dir.Parent;
            }

            return null;
        }
    }
}
