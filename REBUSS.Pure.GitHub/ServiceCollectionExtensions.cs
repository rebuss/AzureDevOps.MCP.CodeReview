using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using REBUSS.Pure.GitHub.Api;
using REBUSS.Pure.GitHub.Configuration;
using REBUSS.Pure.GitHub.Parsers;
using REBUSS.Pure.GitHub.Providers;
using REBUSS.Pure.Core;

namespace REBUSS.Pure.GitHub;

/// <summary>
/// Extension methods for registering GitHub provider services with the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all GitHub provider services: options, authentication,
    /// HTTP client, parsers, providers, and the unified <see cref="IScmClient"/> facade.
    /// </summary>
    public static IServiceCollection AddGitHubProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Options + validation + post-configure resolution
        services.Configure<GitHubOptions>(configuration.GetSection(GitHubOptions.SectionName));
        services.AddSingleton<IValidateOptions<GitHubOptions>, GitHubOptionsValidator>();
        services.AddSingleton<IGitHubRemoteDetector, GitHubRemoteDetector>();
        services.AddSingleton<IGitHubConfigStore, GitHubConfigStore>();
        services.AddSingleton<IPostConfigureOptions<GitHubOptions>, GitHubConfigurationResolver>();

        // Authentication provider (chained: PAT → cached token → GitHub CLI → error)
        services.AddSingleton<IGitHubCliTokenProvider, GitHubCliTokenProvider>();
        services.AddSingleton<IGitHubAuthenticationProvider, GitHubChainedAuthenticationProvider>();
        services.AddTransient<GitHubAuthenticationHandler>();

        // Typed HTTP client with auth handler + resilience
        services.AddHttpClient<IGitHubApiClient, GitHubApiClient>()
            .AddHttpMessageHandler<GitHubAuthenticationHandler>()
            .AddStandardResilienceHandler();

        // GitHub JSON parsers
        services.AddSingleton<IGitHubPullRequestParser, GitHubPullRequestParser>();
        services.AddSingleton<IGitHubFileChangesParser, GitHubFileChangesParser>();

        // GitHub fine-grained providers (internal implementation details)
        services.AddSingleton<GitHubDiffProvider>();
        services.AddSingleton<GitHubMetadataProvider>();
        services.AddSingleton<GitHubFilesProvider>();
        services.AddSingleton<GitHubRepositoryArchiveProvider>();

        // Unified SCM client facade + interface forwarding
        services.AddSingleton<GitHubScmClient>();
        services.AddSingleton<IScmClient>(sp => sp.GetRequiredService<GitHubScmClient>());
        services.AddSingleton<IPullRequestDataProvider>(sp => sp.GetRequiredService<GitHubScmClient>());
        services.AddSingleton<IRepositoryArchiveProvider>(sp => sp.GetRequiredService<GitHubScmClient>());

        return services;
    }
}
