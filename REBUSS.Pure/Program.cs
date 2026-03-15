using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using REBUSS.Pure.AzureDevOpsIntegration.Configuration;
using REBUSS.Pure.AzureDevOpsIntegration.Services;
using REBUSS.Pure.Cli;
using REBUSS.Pure.Logging;
using REBUSS.Pure.Mcp;
using REBUSS.Pure.Mcp.Handlers;
using REBUSS.Pure.Services.Common;
using REBUSS.Pure.Services.Common.Parsers;
using REBUSS.Pure.Services.Content;
using REBUSS.Pure.Services.Diff;
using REBUSS.Pure.Services.FileList;
using REBUSS.Pure.Services.FileList.Classification;
using REBUSS.Pure.Services.Metadata;
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
                    Environment.CurrentDirectory,
                    GetExecutablePath()),
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

        private static string GetLogFilePath()
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "REBUSS.Pure");
            Directory.CreateDirectory(logDir);
            return Path.Combine(logDir, "server.log");
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

            if (!string.IsNullOrWhiteSpace(parseResult.Pat))
                overrides[$"{AzureDevOpsOptions.SectionName}:{nameof(AzureDevOpsOptions.PersonalAccessToken)}"] = parseResult.Pat;

            if (!string.IsNullOrWhiteSpace(parseResult.Organization))
                overrides[$"{AzureDevOpsOptions.SectionName}:{nameof(AzureDevOpsOptions.OrganizationName)}"] = parseResult.Organization;

            if (!string.IsNullOrWhiteSpace(parseResult.Project))
                overrides[$"{AzureDevOpsOptions.SectionName}:{nameof(AzureDevOpsOptions.ProjectName)}"] = parseResult.Project;

            if (!string.IsNullOrWhiteSpace(parseResult.Repository))
                overrides[$"{AzureDevOpsOptions.SectionName}:{nameof(AzureDevOpsOptions.RepositoryName)}"] = parseResult.Repository;

            return overrides;
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
                builder.AddProvider(new FileLoggerProvider(GetLogFilePath()));
                builder.SetMinimumLevel(LogLevel.Debug);
            });

            services.Configure<AzureDevOpsOptions>(configuration.GetSection(AzureDevOpsOptions.SectionName));
            services.AddSingleton<IValidateOptions<AzureDevOpsOptions>, AzureDevOpsOptionsValidator>();

            // Workspace root provider: resolves repository path from CLI --repo, MCP roots, or localRepoPath
            services.AddSingleton<IWorkspaceRootProvider, McpWorkspaceRootProvider>();

            // Configuration resolution: merges explicit config, cached, and auto-detected values
            // via IPostConfigureOptions — runs automatically on first IOptions<AzureDevOpsOptions>.Value access
            services.AddSingleton<IGitRemoteDetector, GitRemoteDetector>();
            services.AddSingleton<ILocalConfigStore, LocalConfigStore>();
            services.AddSingleton<IPostConfigureOptions<AzureDevOpsOptions>, ConfigurationResolver>();

            // Authentication provider (chained: PAT → cached token → Entra ID)
            services.AddSingleton<IAuthenticationProvider, ChainedAuthenticationProvider>();
            services.AddTransient<AuthenticationDelegatingHandler>();

            services.AddHttpClient<IAzureDevOpsApiClient, AzureDevOpsApiClient>()
            .AddHttpMessageHandler<AuthenticationDelegatingHandler>()
            .AddStandardResilienceHandler();

            services.AddSingleton<IPullRequestMetadataParser, PullRequestMetadataParser>();
            services.AddSingleton<IIterationInfoParser, IterationInfoParser>();
            services.AddSingleton<IFileChangesParser, FileChangesParser>();
            services.AddSingleton<IDiffAlgorithm, LcsDiffAlgorithm>();
            services.AddSingleton<IStructuredDiffBuilder, StructuredDiffBuilder>();
            services.AddSingleton<IPullRequestDiffProvider, AzureDevOpsDiffProvider>();
            services.AddSingleton<IPullRequestMetadataProvider, AzureDevOpsMetadataProvider>();
            services.AddSingleton<IFileClassifier, FileClassifier>();
            services.AddSingleton<IPullRequestFilesProvider, AzureDevOpsFilesProvider>();
            services.AddSingleton<IFileContentProvider, AzureDevOpsFileContentProvider>();
            services.AddSingleton<IMcpToolHandler, GetPullRequestDiffToolHandler>();
            services.AddSingleton<IMcpToolHandler, GetFileDiffToolHandler>();
            services.AddSingleton<IMcpToolHandler, GetPullRequestMetadataToolHandler>();
            services.AddSingleton<IMcpToolHandler, GetPullRequestFilesToolHandler>();
            services.AddSingleton<IMcpToolHandler, GetFileContentAtRefToolHandler>();

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
    }
}
