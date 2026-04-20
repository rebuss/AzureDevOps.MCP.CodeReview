using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using REBUSS.Pure.AzureDevOps;
using REBUSS.Pure.AzureDevOps.Configuration;
using REBUSS.Pure.Cli;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.GitHub;
using REBUSS.Pure.GitHub.Configuration;
using REBUSS.Pure.Logging;
using REBUSS.Pure.Core.Services.CopilotReview;
using REBUSS.Pure.Services;
using REBUSS.Pure.Services.ContextWindow;
using REBUSS.Pure.Services.CopilotReview;
using REBUSS.Pure.Services.LocalReview;
using REBUSS.Pure.Services.PrEnrichment;
using AzureDevOpsNames = REBUSS.Pure.AzureDevOps.Names;
using GitHubNames = REBUSS.Pure.GitHub.Names;
using ResponsePacking = REBUSS.Pure.Services.ResponsePacking;
using Pagination = REBUSS.Pure.Services.Pagination;
using REBUSS.Pure.Properties;
using REBUSS.Pure.RoslynProcessor;
using REBUSS.Pure.Services.RepositoryDownload;

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
                var cmd when cmd == Resources.CliCommandInit => new InitCommand(
                    Console.Error,
                    Console.In,
                    Environment.CurrentDirectory,
                    GetExecutablePath(),
                    parseResult.Pat,
                    parseResult.IsGlobal,
                    parseResult.Ide,
                    parseResult.Agent,
                    detectedProvider: null,
                    processRunner: null,
                    localConfigStore: new AzureDevOps.Configuration.LocalConfigStore(NullLogger<AzureDevOps.Configuration.LocalConfigStore>.Instance),
                    gitHubConfigStore: new GitHub.Configuration.GitHubConfigStore(NullLogger<GitHub.Configuration.GitHubConfigStore>.Instance)),
                _ => throw new InvalidOperationException(string.Format(Resources.ErrorUnknownCommand, parseResult.CommandName))
            };

            return await command.ExecuteAsync();
        }

        private static string GetExecutablePath()
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(processPath))
                return processPath;

            return Path.Combine(AppContext.BaseDirectory, AppConstants.ExecutableName);
        }

        private static string GetLogDirectory()
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppConstants.ServerName);
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
                    .AddJsonFile(Resources.AppSettingsFileName, optional: true, reloadOnChange: true)
                    .AddJsonFile(Resources.AppSettingsLocalFileName, optional: true, reloadOnChange: true)
                    .AddEnvironmentVariables()
                    .AddInMemoryCollection(cliOverrides)
                    .Build();

                var builder = Host.CreateApplicationBuilder();

                // Replace the host's default configuration with our pre-built one
                builder.Services.AddSingleton<IConfiguration>(configuration);

                // Configure logging: all output to stderr (Constitution Principle II).
                // The `Logging:LogLevel` rules from appsettings.json must be wired to our
                // explicit configuration — Host.CreateApplicationBuilder() reads from the
                // process working directory which, for MCP servers spawned by an IDE, is not
                // the executable directory. Without this line our LogLevel filters silently
                // never apply and every category logs at Information.
                builder.Logging.ClearProviders();
                builder.Logging.AddConfiguration(configuration.GetSection("Logging"));
                builder.Logging.AddConsole(options =>
                {
                    options.LogToStandardErrorThreshold = LogLevel.Trace;
                });
                builder.Logging.AddProvider(new FileLoggerProvider(GetLogDirectory()));

                // Register business services (providers, algorithms, shared services)
                ConfigureBusinessServices(builder.Services, configuration, parseResult.RepoPath, parseResult.Agent);

                // Add MCP server with stdio transport and tool discovery
                builder.Services
                    .AddMcpServer(options =>
                    {
                        options.ServerInfo = new() { Name = AppConstants.ServerName, Version = Resources.ServerVersion };
                    })
                    .WithStdioServerTransport()
                    .WithToolsFromAssembly();

                using var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    cts.Cancel();
                };

                var app = builder.Build();

                // Apply CLI --repo argument if provided
                if (!string.IsNullOrWhiteSpace(parseResult.RepoPath))
                {
                    var workspaceRootProvider = app.Services.GetRequiredService<IWorkspaceRootProvider>();
                    workspaceRootProvider.SetCliRepositoryPath(parseResult.RepoPath);
                }

                await app.RunAsync(cts.Token);
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync(string.Format(Resources.ErrorFatalMessage, AppConstants.ServerName, ex.GetType().FullName, ex.Message));
                if (ex.InnerException is not null)
                    await Console.Error.WriteLineAsync(string.Format(Resources.ErrorInnerMessage, AppConstants.ServerName, ex.InnerException.GetType().FullName, ex.InnerException.Message));
                await Console.Error.WriteLineAsync(ex.StackTrace ?? string.Empty);
                Environment.Exit(1);
            }
        }

        private static void ConfigureBusinessServices(IServiceCollection services, IConfiguration configuration, string? repoPath = null, string? agent = null)
        {
            // Workspace root provider: resolves repository path from CLI --repo, MCP roots, or localRepoPath
            services.AddSingleton<IWorkspaceRootProvider, McpWorkspaceRootProvider>();

            // Shared services (provider-agnostic)
            services.AddSingleton<IDiffAlgorithm, DiffPlexDiffAlgorithm>();
            services.AddSingleton<IStructuredDiffBuilder, StructuredDiffBuilder>();
            services.AddSingleton<IFileClassifier, FileClassifier>();
            services.AddSingleton<DiffSourceResolver>();
            services.AddSingleton<IDiffEnricher, BeforeAfterEnricher>();         // Order=100
            services.AddSingleton<IDiffEnricher, ScopeAnnotatorEnricher>();      // Order=150
            services.AddSingleton<IDiffEnricher, StructuralChangeEnricher>();    // Order=200
            services.AddSingleton<IDiffEnricher, UsingsChangeEnricher>();        // Order=250
            services.AddSingleton<CallSiteScanner>();
            services.AddSingleton<IDiffEnricher, CallSiteEnricher>();            // Order=300
            services.AddSingleton<IDiffEnricher, FileStructureValidationEnricher>(); // Order=400
            services.AddSingleton<ICodeProcessor, CompositeCodeProcessor>();

            // Progress reporting (MCP notifications/progress)
            services.AddSingleton<IProgressReporter, ProgressReporter>();

            // Workflow timeouts (progressive PR metadata feature)
            services.Configure<WorkflowOptions>(configuration.GetSection(WorkflowOptions.SectionName));
            services.AddSingleton<IPrEnrichmentOrchestrator, PrEnrichmentOrchestrator>();

            // Copilot Review Layer (feature 013) — SDK-backed server-side PR review.
            // The provider is registered three ways: (1) concrete singleton, (2) interface
            // alias so consumers can depend on ICopilotClientProvider, (3) IHostedService so
            // the generic host calls StopAsync on shutdown. All three resolve to the same instance.
            services.Configure<CopilotReviewOptions>(configuration.GetSection(CopilotReviewOptions.SectionName));
            services.AddSingleton<ICopilotTokenResolver, CopilotTokenResolver>();
            services.AddSingleton<CopilotVerificationRunner>();
            services.AddSingleton<ICopilotVerificationProbe>(sp => sp.GetRequiredService<CopilotVerificationRunner>());
            services.AddSingleton<CopilotClientProvider>();
            services.AddSingleton<ICopilotClientProvider>(sp => sp.GetRequiredService<CopilotClientProvider>());
            services.AddHostedService(sp => sp.GetRequiredService<CopilotClientProvider>());
            services.AddSingleton<CopilotRequestThrottle>();
            services.AddSingleton<ICopilotSessionFactory, CopilotSessionFactory>();

            // IAgentInvoker — one-shot prompt→text abstraction over Copilot SDK or Claude CLI.
            // Selection is driven by --agent on the command line (carried through mcp.json args);
            // when the flag is absent, Copilot is the default to preserve existing behavior.
            // TODO (follow-up): thread IAgentInvoker through CopilotPageReviewer and
            // FindingValidator so the Claude path can actually drive the review orchestrator
            // at runtime. Without that wiring, selecting --agent claude currently only affects
            // init-time config and setup — runtime review still uses the Copilot code path.
            if (string.Equals(agent, CliArgumentParser.AgentClaude, StringComparison.OrdinalIgnoreCase))
            {
                services.AddSingleton<REBUSS.Pure.Core.Services.AgentInvocation.IAgentInvoker,
                    REBUSS.Pure.Services.AgentInvocation.ClaudeCliAgentInvoker>();
            }
            else
            {
                services.AddSingleton<REBUSS.Pure.Core.Services.AgentInvocation.IAgentInvoker,
                    REBUSS.Pure.Services.AgentInvocation.CopilotAgentInvoker>();
            }

            services.AddSingleton<ICopilotAvailabilityDetector, CopilotAvailabilityDetector>();
            services.AddSingleton<ICopilotPageReviewer, CopilotPageReviewer>();

            // Feature 022 — Copilot inspection (internal diagnostic, env-var gated).
            // REBUSS_COPILOT_INSPECT=1|true|True registers the filesystem writer; any other
            // value registers a no-op. Read once at DI composition time; restart to toggle.
            var inspectEnabled = Environment.GetEnvironmentVariable("REBUSS_COPILOT_INSPECT")
                is "1" or "true" or "True";
            if (inspectEnabled)
            {
                services.AddSingleton<
                    REBUSS.Pure.Services.CopilotReview.Inspection.ICopilotInspectionWriter,
                    REBUSS.Pure.Services.CopilotReview.Inspection.FileSystemCopilotInspectionWriter>();
            }
            else
            {
                services.AddSingleton<
                    REBUSS.Pure.Services.CopilotReview.Inspection.ICopilotInspectionWriter,
                    REBUSS.Pure.Services.CopilotReview.Inspection.NoOpCopilotInspectionWriter>();
            }

            // Feature 021 — Finding validation pipeline (false positive reduction).
            // Registered unconditionally; CopilotReviewOrchestrator short-circuits at
            // runtime based on CopilotReviewOptions.ValidateFindings (per Principle V
            // deferred resolution — the flag is read at first review, not at DI time).
            services.AddSingleton<REBUSS.Pure.Services.CopilotReview.Validation.FindingScopeResolver>();
            services.AddSingleton<REBUSS.Pure.Services.CopilotReview.Validation.FindingValidator>();

            services.AddSingleton<ICopilotReviewOrchestrator, CopilotReviewOrchestrator>();
            services.AddSingleton<CopilotReviewWaiter>();

            // Context Window Awareness
            services.Configure<ContextWindowOptions>(configuration.GetSection(ContextWindowOptions.SectionName));

            services.AddSingleton<IContextBudgetResolver, ContextBudgetResolver>();
            services.AddSingleton<ITokenEstimator, TokenEstimator>();

            // Response Packing
            services.AddSingleton<IResponsePacker, ResponsePacking.ResponsePacker>();

            // Deterministic Pagination (Feature 004)
            services.AddSingleton<IPageAllocator, Pagination.PageAllocator>();
            services.AddSingleton<IPageReferenceCodec, Pagination.PageReferenceCodec>();

            // PR diff cache (eliminates duplicate API calls between metadata and content)
            services.AddSingleton<IPullRequestDiffCache, PullRequestDiffCache>();

            // Provider selection: explicit config > auto-detection from git remote
            var provider = DetectProvider(configuration, repoPath);
            switch (provider)
            {
                case GitHubNames.Provider:
                    services.AddGitHubProvider(configuration);
                    break;
                case AzureDevOpsNames.Provider:
                default:
                    services.AddAzureDevOpsProvider(configuration);
                    break;
            }

            // Repository download orchestrator + startup cleanup
            services.AddSingleton<IRepositoryDownloadOrchestrator, RepositoryDownloadOrchestrator>();
            services.AddHostedService<RepositoryCleanupService>();

            // Local self-review pipeline
            services.AddSingleton<ILocalGitClient, LocalGitClient>();
            services.AddSingleton<ILocalReviewProvider, LocalReviewProvider>();
            services.AddSingleton<ILocalEnrichmentOrchestrator, LocalEnrichmentOrchestrator>();
        }

        private static Dictionary<string, string?> BuildCliConfigOverrides(CliParseResult parseResult)
        {
            var overrides = new Dictionary<string, string?>();

            if (!string.IsNullOrWhiteSpace(parseResult.Provider))
                overrides[Resources.ConfigKeyProvider] = parseResult.Provider;

            // Determine which provider should receive the PAT based on CLI context
            var patTarget = ResolvePatTarget(parseResult);

            if (!string.IsNullOrWhiteSpace(parseResult.Pat))
            {
                if (patTarget is null || string.Equals(patTarget, AzureDevOpsNames.Provider, StringComparison.OrdinalIgnoreCase))
                    overrides[$"{AzureDevOpsOptions.SectionName}:{nameof(AzureDevOpsOptions.PersonalAccessToken)}"] = parseResult.Pat;
                if (patTarget is null || string.Equals(patTarget, GitHubNames.Provider, StringComparison.OrdinalIgnoreCase))
                    overrides[$"{GitHubOptions.SectionName}:{nameof(GitHubOptions.PersonalAccessToken)}"] = parseResult.Pat;
            }

            if (!string.IsNullOrWhiteSpace(parseResult.Organization))
                overrides[$"{AzureDevOpsOptions.SectionName}:{nameof(AzureDevOpsOptions.OrganizationName)}"] = parseResult.Organization;

            if (!string.IsNullOrWhiteSpace(parseResult.Project))
                overrides[$"{AzureDevOpsOptions.SectionName}:{nameof(AzureDevOpsOptions.ProjectName)}"] = parseResult.Project;

            if (!string.IsNullOrWhiteSpace(parseResult.Repository))
            {
                if (patTarget is null || string.Equals(patTarget, AzureDevOpsNames.Provider, StringComparison.OrdinalIgnoreCase))
                    overrides[$"{AzureDevOpsOptions.SectionName}:{nameof(AzureDevOpsOptions.RepositoryName)}"] = parseResult.Repository;
                if (patTarget is null || string.Equals(patTarget, GitHubNames.Provider, StringComparison.OrdinalIgnoreCase))
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
                return GitHubNames.Provider;
            if (!string.IsNullOrWhiteSpace(parseResult.Organization))
                return AzureDevOpsNames.Provider;
            return null;
        }

        /// <summary>
        /// Determines the SCM provider to use based on configuration, then git remote auto-detection.
        /// Priority: explicit "Provider" key > GitHub config section populated > AzureDevOps config section populated > git remote URL > default (AzureDevOps).
        /// </summary>
        internal static string DetectProvider(IConfiguration configuration, string? repoPath = null)
        {
            // 1. Explicit provider setting (normalized to canonical casing)
            var explicitProvider = configuration.GetValue<string>(Resources.ConfigKeyProvider);
            if (!string.IsNullOrWhiteSpace(explicitProvider))
                return explicitProvider.ToLowerInvariant() switch
                {
                    GitHubNames.ProviderLower => GitHubNames.Provider,
                    AzureDevOpsNames.ProviderLower => AzureDevOpsNames.Provider,
                    _ => explicitProvider
                };

            // 2. Check if GitHub section has owner configured
            var githubOwner = configuration[$"{GitHubOptions.SectionName}:{nameof(GitHubOptions.Owner)}"];
            if (!string.IsNullOrWhiteSpace(githubOwner))
                return GitHubNames.Provider;

            // 3. Check if AzureDevOps section has organization configured
            var adoOrg = configuration[$"{AzureDevOpsOptions.SectionName}:{nameof(AzureDevOpsOptions.OrganizationName)}"];
            if (!string.IsNullOrWhiteSpace(adoOrg))
                return AzureDevOpsNames.Provider;

            // 4. Auto-detect from git remote URL
            var remoteUrl = GetGitRemoteUrl(repoPath);
            if (remoteUrl is not null)
            {
                if (remoteUrl.Contains(GitHubNames.Domain, StringComparison.OrdinalIgnoreCase))
                    return GitHubNames.Provider;

                if (remoteUrl.Contains(AzureDevOpsNames.Domain, StringComparison.OrdinalIgnoreCase) ||
                    remoteUrl.Contains(AzureDevOpsNames.LegacyDomain, StringComparison.OrdinalIgnoreCase))
                    return AzureDevOpsNames.Provider;
            }

            // 5. Default
            return AzureDevOpsNames.Provider;
        }

        private static string? GetGitRemoteUrl(string? workingDirectory = null)
        {
            try
            {
                using var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = Resources.GitExecutable,
                        Arguments = Resources.GitRemoteGetUrlArgs,
                        WorkingDirectory = workingDirectory ?? string.Empty,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                process.StandardInput.Close();

                if (!process.WaitForExit(TimeSpan.FromSeconds(5)))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return null;
                }

                var output = process.StandardOutput.ReadToEnd();
                return process.ExitCode == 0 ? output.Trim() : null;
            }
            catch
            {
                return null;
            }
        }

    }
}
