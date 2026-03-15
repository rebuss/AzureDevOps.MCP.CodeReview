using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace REBUSS.Pure.Mcp
{
    /// <summary>
    /// Resolves the workspace repository root path using (in priority order):
    /// <list type="number">
    ///   <item>CLI <c>--repo</c> argument (highest priority).</item>
    ///   <item>MCP roots sent by the client during initialization.</item>
    ///   <item><c>LocalRepoPath</c> configuration fallback.</item>
    /// </list>
    /// Resolution is lazy — it only happens when <see cref="ResolveRepositoryRoot"/> is called.
    /// Reads <c>LocalRepoPath</c> directly from <see cref="IConfiguration"/> to avoid a
    /// circular dependency with <c>IPostConfigureOptions&lt;AzureDevOpsOptions&gt;</c>.
    /// </summary>
    public class McpWorkspaceRootProvider : IWorkspaceRootProvider
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<McpWorkspaceRootProvider> _logger;
        private List<string> _rootUris = new();
        private string? _cliRepositoryPath;
        private readonly object _lock = new();

        public McpWorkspaceRootProvider(
            IConfiguration configuration,
            ILogger<McpWorkspaceRootProvider> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        public void SetCliRepositoryPath(string path)
        {
            if (IsUnexpandedVariable(path))
            {
                _logger.LogWarning(
                    "CLI --repo value '{Path}' looks like an unexpanded variable (e.g. from VS Code on a non-VS Code client). " +
                    "Ignoring it — MCP roots or localRepoPath will be used instead.", path);
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
            }

            _logger.LogDebug("MCP roots received: {RootCount} root(s)", rootUris.Count);
            foreach (var uri in rootUris)
            {
                _logger.LogDebug("  Root: {RootUri}", uri);
            }
        }

        public IReadOnlyList<string> GetRootUris()
        {
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

            // 2. Try MCP roots
            var rootUris = GetRootUris();
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
            var localRepoPath = _configuration.GetSection("AzureDevOps")["LocalRepoPath"];
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

        internal static string? ConvertUriToLocalPath(string uri)
        {
            if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) &&
                string.Equals(parsed.Scheme, "file", StringComparison.OrdinalIgnoreCase))
            {
                return parsed.LocalPath;
            }

            return null;
        }

        internal static string? FindGitRepositoryRoot(string startDirectory)
        {
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
