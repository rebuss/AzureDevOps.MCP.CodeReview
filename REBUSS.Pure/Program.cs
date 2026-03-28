using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.AzureDevOps;
using REBUSS.Pure.AzureDevOps.Configuration;
using REBUSS.Pure.Cli;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.GitHub;
using REBUSS.Pure.GitHub.Configuration;
using REBUSS.Pure.Logging;
using REBUSS.Pure.Mcp;
using REBUSS.Pure.Mcp.Handlers;
using REBUSS.Pure.Services.ContextWindow;
using REBUSS.Pure.Services.LocalReview;
using ResponsePacking = REBUSS.Pure.Services.ResponsePacking;
using Pagination = REBUSS.Pure.Services.Pagination;
using REBUSS.Pure.Tools;

namespace REBUSS.Pure
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            var parseResult = CliArgumentParser.Parse(args);

            if (!parseResult.IsServerMode)
                return await RunCliCommandAsync(parseResult);

            await RunMcpServerAsync(parseResult);
            return 0;
        }

        private static async Task<int> RunCliCommandAsync(CliParseResult parseResult)
        {
            ICliCommand command = parseResult.CommandName switch
            {
                "init" => new InitCommand(
                    Console.Error,
                    Console.In,
                    Environment.CurrentDirectory,
                    GetExecutablePath(),
                    parseResult.Pat),
                _ => throw new InvalidOperationException($"Unknown command: {parseResult.CommandName}")
            };

            return await command.ExecuteAsync();
        }

        private static string GetExecutablePath()
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(processPath))
                return processPath;

            return Path.Combine(AppContext.BaseDirectory, "REBUSS.Pure.exe");
        }

        private static string GetLogDirectory()
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "REBUSS.Pure");
            Directory.CreateDirectory(logDir);
            return logDir;
        }

        private static async Task RunMcpServerAsync(CliParseResult parseResult)
        {
            try
            {
                var cliOverrides = BuildCliConfigOverrides(parseResult);

                var configuration = new ConfigurationBuilder()
                    .SetBasePath(AppContext.BaseDirectory)
                    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                    .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true)
                    .AddEnvironmentVariables()
                    .AddInMemoryCollection(cliOverrides)
                    .Build();

                var services = new ServiceCollection();
                ConfigureServices(services, configuration);
                await using var serviceProvider = services.BuildServiceProvider();

                // Apply CLI --repo argument if provided
                if (!string.IsNullOrWhiteSpace(parseResult.RepoPath))
                {
                    var workspaceRootProvider = serviceProvider.GetRequiredService<IWorkspaceRootProvider>();
                    workspaceRootProvider.SetCliRepositoryPath(parseResult.RepoPath);
                }

                var server = serviceProvider.GetRequiredService<McpServer>();
                using var cts = new CancellationTokenSource();

                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    cts.Cancel();
                };

                await server.RunAsync(cts.Token);
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"[REBUSS.Pure] FATAL: {ex.GetType().FullName}: {ex.Message}");
                if (ex.InnerException is not null)
                    await Console.Error.WriteLineAsync($"[REBUSS.Pure] INNER: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}");
                await Console.Error.WriteLineAsync(ex.StackTrace ?? string.Empty);
                Environment.Exit(1);
            }
        }

        private static Dictionary<string, string?> BuildCliConfigOverrides(CliParseResult parseResult)
        {
            var overrides = new Dictionary<string, string?>();

            if (!string.IsNullOrWhiteSpace(parseResult.Provider))
                overrides["Provider"] = parseResult.Provider;

            // Determine which provider should receive the PAT based on CLI context
            var patTarget = ResolvePatTarget(parseResult);

            if (!string.IsNullOrWhiteSpace(parseResult.Pat))
            {
                if (patTarget is null || string.Equals(patTarget, "AzureDevOps", StringComparison.OrdinalIgnoreCase))
                    overrides[$"{AzureDevOpsOptions.SectionName}:{nameof(AzureDevOpsOptions.PersonalAccessToken)}"] = parseResult.Pat;
                if (patTarget is null || string.Equals(patTarget, "GitHub", StringComparison.OrdinalIgnoreCase))
                    overrides[$"{GitHubOptions.SectionName}:{nameof(GitHubOptions.PersonalAccessToken)}"] = parseResult.Pat;
            }

            if (!string.IsNullOrWhiteSpace(parseResult.Organization))
                overrides[$"{AzureDevOpsOptions.SectionName}:{nameof(AzureDevOpsOptions.OrganizationName)}"] = parseResult.Organization;

            if (!string.IsNullOrWhiteSpace(parseResult.Project))
                overrides[$"{AzureDevOpsOptions.SectionName}:{nameof(AzureDevOpsOptions.ProjectName)}"] = parseResult.Project;

            if (!string.IsNullOrWhiteSpace(parseResult.Repository))
            {
                if (patTarget is null || string.Equals(patTarget, "AzureDevOps", StringComparison.OrdinalIgnoreCase))
                    overrides[$"{AzureDevOpsOptions.SectionName}:{nameof(AzureDevOpsOptions.RepositoryName)}"] = parseResult.Repository;
                if (patTarget is null || string.Equals(patTarget, "GitHub", StringComparison.OrdinalIgnoreCase))
                    overrides[$"{GitHubOptions.SectionName}:{nameof(GitHubOptions.RepositoryName)}"] = parseResult.Repository;
            }

            // GitHub CLI overrides
            if (!string.IsNullOrWhiteSpace(parseResult.Owner))
                overrides[$"{GitHubOptions.SectionName}:{nameof(GitHubOptions.Owner)}"] = parseResult.Owner;

            return overrides;
        }

        /// <summary>
        /// Infers which provider the CLI arguments are targeting so that secrets
        /// (e.g. PAT) are only written to the relevant configuration section.
        /// Returns <c>null</c> when the target cannot be determined.
        /// </summary>
        private static string? ResolvePatTarget(CliParseResult parseResult)
        {
            if (!string.IsNullOrWhiteSpace(parseResult.Provider))
                return parseResult.Provider;
            if (!string.IsNullOrWhiteSpace(parseResult.Owner))
                return "GitHub";
            if (!string.IsNullOrWhiteSpace(parseResult.Organization))
                return "AzureDevOps";
            return null;
        }

        private static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        {
            // Register IConfiguration so it can be injected directly (e.g. by McpWorkspaceRootProvider)
            services.AddSingleton<IConfiguration>(configuration);

            services.AddLogging(builder =>
            {
                builder.AddConsole(options =>
                {
                    options.LogToStandardErrorThreshold = LogLevel.Trace;
                });
                builder.AddProvider(new FileLoggerProvider(GetLogDirectory()));
                builder.SetMinimumLevel(LogLevel.Debug);
            });

            // Workspace root provider: resolves repository path from CLI --repo, MCP roots, or localRepoPath
            services.AddSingleton<IWorkspaceRootProvider, McpWorkspaceRootProvider>();

            // Shared services (provider-agnostic)
            services.AddSingleton<IDiffAlgorithm, LcsDiffAlgorithm>();
            services.AddSingleton<IStructuredDiffBuilder, StructuredDiffBuilder>();
            services.AddSingleton<IFileClassifier, FileClassifier>();

            // Context Window Awareness
            services.Configure<ContextWindowOptions>(configuration.GetSection(ContextWindowOptions.SectionName));
            services.AddSingleton<IContextBudgetResolver, ContextBudgetResolver>();
            services.AddSingleton<ITokenEstimator, TokenEstimator>();

            // Response Packing
            services.AddSingleton<IResponsePacker, ResponsePacking.ResponsePacker>();

            // Deterministic Pagination (Feature 004)
            services.AddSingleton<IPageAllocator, Pagination.PageAllocator>();
            services.AddSingleton<IPageReferenceCodec, Pagination.PageReferenceCodec>();

            // Provider selection: explicit config > auto-detection from git remote
            var provider = DetectProvider(configuration);
            switch (provider)
            {
                case "GitHub":
                    services.AddGitHubProvider(configuration);
                    break;
                case "AzureDevOps":
                default:
                    services.AddAzureDevOpsProvider(configuration);
                    break;
            }

            services.AddSingleton<IMcpToolHandler, GetPullRequestDiffToolHandler>();
            services.AddSingleton<IMcpToolHandler, GetFileDiffToolHandler>();
            services.AddSingleton<IMcpToolHandler, GetPullRequestMetadataToolHandler>();
            services.AddSingleton<IMcpToolHandler, GetPullRequestFilesToolHandler>();
            services.AddSingleton<IMcpToolHandler, GetFileContentAtRefToolHandler>();

            // Local self-review pipeline
            services.AddSingleton<ILocalGitClient, LocalGitClient>();
            services.AddSingleton<ILocalReviewProvider, LocalReviewProvider>();
            services.AddSingleton<IMcpToolHandler, GetLocalChangesFilesToolHandler>();
            services.AddSingleton<IMcpToolHandler, GetLocalFileDiffToolHandler>();

            // JSON-RPC infrastructure
            services.AddSingleton<IJsonRpcSerializer, SystemTextJsonSerializer>();
            services.AddSingleton<IJsonRpcTransport>(_ =>
                new StreamJsonRpcTransport(Console.OpenStandardInput(), Console.OpenStandardOutput()));

            // Method handlers — each handles one JSON-RPC method (OCP: add new methods without changing McpServer)
            services.AddSingleton<IMcpMethodHandler, InitializeMethodHandler>();
            services.AddSingleton<IMcpMethodHandler, ToolsListMethodHandler>();
            services.AddSingleton<IMcpMethodHandler, ToolsCallMethodHandler>();

            services.AddSingleton<McpServer>(sp => new McpServer(
                sp.GetRequiredService<ILogger<McpServer>>(),
                sp.GetRequiredService<IEnumerable<IMcpMethodHandler>>(),
                sp.GetRequiredService<IJsonRpcTransport>(),
                sp.GetRequiredService<IJsonRpcSerializer>()));
        }

        /// <summary>
        /// Determines the SCM provider to use based on configuration, then git remote auto-detection.
        /// Priority: explicit "Provider" key > GitHub config section populated > AzureDevOps config section populated > git remote URL > default (AzureDevOps).
        /// </summary>
        internal static string DetectProvider(IConfiguration configuration)
        {
            // 1. Explicit provider setting (normalized to canonical casing)
            var explicitProvider = configuration.GetValue<string>("Provider");
            if (!string.IsNullOrWhiteSpace(explicitProvider))
                return explicitProvider.ToLowerInvariant() switch
                {
                    "github" => "GitHub",
                    "azuredevops" => "AzureDevOps",
                    _ => explicitProvider
                };

            // 2. Check if GitHub section has owner configured
            var githubOwner = configuration[$"{GitHubOptions.SectionName}:{nameof(GitHubOptions.Owner)}"];
            if (!string.IsNullOrWhiteSpace(githubOwner))
                return "GitHub";

            // 3. Check if AzureDevOps section has organization configured
            var adoOrg = configuration[$"{AzureDevOpsOptions.SectionName}:{nameof(AzureDevOpsOptions.OrganizationName)}"];
            if (!string.IsNullOrWhiteSpace(adoOrg))
                return "AzureDevOps";

            // 4. Auto-detect from git remote URL
            var remoteUrl = GetGitRemoteUrl();
            if (remoteUrl is not null)
            {
                if (remoteUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase))
                    return "GitHub";

                if (remoteUrl.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase) ||
                    remoteUrl.Contains("visualstudio.com", StringComparison.OrdinalIgnoreCase))
                    return "AzureDevOps";
            }

            // 5. Default
            return "AzureDevOps";
        }

        private static string? GetGitRemoteUrl()
        {
            try
            {
                using var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "git",
                        Arguments = "remote get-url origin",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(TimeSpan.FromSeconds(5));

                return process.ExitCode == 0 ? output.Trim() : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
