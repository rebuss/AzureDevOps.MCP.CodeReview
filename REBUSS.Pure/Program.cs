using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using REBUSS.Pure.AzureDevOpsIntegration.Configuration;
using REBUSS.Pure.AzureDevOpsIntegration.Services;
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
using System.Net.Http.Headers;
using System.Text;

namespace REBUSS.Pure
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

            var services = new ServiceCollection();
            ConfigureServices(services, configuration);
            var serviceProvider = services.BuildServiceProvider();

            // Fail fast if Azure DevOps configuration is incomplete
            _ = serviceProvider.GetRequiredService<IOptions<AzureDevOpsOptions>>().Value;

            var server = serviceProvider.GetRequiredService<McpServer>();
            using var cts = new CancellationTokenSource();

            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            try
            {
                await server.RunAsync(cts.Token);
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"Fatal error: {ex.Message}");
                await Console.Error.WriteLineAsync(ex.StackTrace);
                Environment.Exit(1);
            }
        }

        private static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        {
            services.AddLogging(builder =>
            {
                builder.AddConsole(options =>
                {
                    options.LogToStandardErrorThreshold = LogLevel.Trace;
                });
                builder.SetMinimumLevel(LogLevel.Information);
            });

            services.Configure<AzureDevOpsOptions>(configuration.GetSection(AzureDevOpsOptions.SectionName));
            services.AddSingleton<IValidateOptions<AzureDevOpsOptions>, AzureDevOpsOptionsValidator>();

            services.AddHttpClient<IAzureDevOpsApiClient, AzureDevOpsApiClient>((sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<AzureDevOpsOptions>>().Value;
                var base64Pat = Convert.ToBase64String(
                    Encoding.ASCII.GetBytes($":{options.PersonalAccessToken}"));
                client.BaseAddress = new Uri($"https://dev.azure.com/{options.OrganizationName}/");
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Basic", base64Pat);
            })
            .AddStandardResilienceHandler();

            services.AddSingleton<IPullRequestMetadataParser, PullRequestMetadataParser>();
            services.AddSingleton<IIterationInfoParser, IterationInfoParser>();
            services.AddSingleton<IFileChangesParser, FileChangesParser>();
            services.AddSingleton<IDiffAlgorithm, LcsDiffAlgorithm>();
            services.AddSingleton<IUnifiedDiffBuilder, UnifiedDiffBuilder>();
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
