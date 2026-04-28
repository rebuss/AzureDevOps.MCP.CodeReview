using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using REBUSS.Pure.AzureDevOps.Api;
using REBUSS.Pure.AzureDevOps.Configuration;
using REBUSS.Pure.AzureDevOps.Parsers;
using REBUSS.Pure.AzureDevOps.Providers;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Shared;

namespace REBUSS.Pure.AzureDevOps;

/// <summary>
/// Extension methods for registering Azure DevOps provider services with the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all Azure DevOps provider services: options, authentication,
    /// HTTP client, parsers, providers, and the unified <see cref="IScmClient"/> facade.
    /// </summary>
    public static IServiceCollection AddAzureDevOpsProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Options + validation + post-configure resolution
        services.Configure<AzureDevOpsOptions>(configuration.GetSection(AzureDevOpsOptions.SectionName));
        services.Configure<AzureDevOpsDiffOptions>(configuration.GetSection(AzureDevOpsDiffOptions.SectionName));
        services.AddSingleton<IValidateOptions<AzureDevOpsOptions>, AzureDevOpsOptionsValidator>();
        services.AddSingleton<IGitRemoteDetector, GitRemoteDetector>();
        services.AddSingleton<ILocalConfigStore, LocalConfigStore>();
        services.AddSingleton<IPostConfigureOptions<AzureDevOpsOptions>, ConfigurationResolver>();

        // Authentication provider (chained: PAT → cached token → Azure CLI → error)
        services.AddSingleton<IAzureCliTokenProvider, AzureCliTokenProvider>();
        services.AddSingleton<IAuthenticationProvider, ChainedAuthenticationProvider>();
        services.AddTransient<AuthenticationDelegatingHandler>();

        // Typed HTTP client with auth handler + resilience
        services.AddHttpClient<IAzureDevOpsApiClient, AzureDevOpsApiClient>()
            .AddHttpMessageHandler<AuthenticationDelegatingHandler>()
            .AddStandardResilienceHandler();

        // Azure DevOps JSON parsers
        services.AddSingleton<IPullRequestMetadataParser, PullRequestMetadataParser>();
        services.AddSingleton<IIterationInfoParser, IterationInfoParser>();
        services.AddSingleton<IFileChangesParser, FileChangesParser>();

        // Azure DevOps fine-grained providers (internal implementation details)
        services.AddSingleton<Providers.Diff.DiffSkipPolicy>();
        services.AddSingleton<Providers.Diff.DiffSourcePairFactory>();
        services.AddSingleton<Providers.Diff.PrDataFetcher>();
        // AzureDevOpsDiffProvider has an internal constructor (its parameter list contains
        // internal Diff.* collaborator types). Microsoft.Extensions.DependencyInjection's
        // default activator inspects Type.GetConstructors() which returns ONLY public
        // constructors, so it cannot locate the internal ctor and resolution fails at
        // first GetService call. Register via an explicit factory delegate that runs in
        // this same assembly (where the internal ctor is visible) — keeps the ctor and
        // collaborator types internal without leaking them into the public API surface.
        services.AddSingleton<AzureDevOpsDiffProvider>(sp => new AzureDevOpsDiffProvider(
            sp.GetRequiredService<Providers.Diff.PrDataFetcher>(),
            sp.GetRequiredService<IStructuredDiffBuilder>(),
            sp.GetRequiredService<Providers.Diff.DiffSkipPolicy>(),
            sp.GetRequiredService<Providers.Diff.DiffSourcePairFactory>(),
            sp.GetRequiredService<ILogger<AzureDevOpsDiffProvider>>()));
        services.AddSingleton<AzureDevOpsMetadataProvider>();
        services.AddSingleton<AzureDevOpsFilesProvider>();
        services.AddSingleton<AzureDevOpsRepositoryArchiveProvider>();

        // Unified SCM client facade + interface forwarding
        services.AddSingleton<AzureDevOpsScmClient>();
        services.AddSingleton<IScmClient>(sp => sp.GetRequiredService<AzureDevOpsScmClient>());
        services.AddSingleton<IPullRequestDataProvider>(sp => sp.GetRequiredService<AzureDevOpsScmClient>());
        services.AddSingleton<IRepositoryArchiveProvider>(sp => sp.GetRequiredService<AzureDevOpsScmClient>());

        return services;
    }
}
