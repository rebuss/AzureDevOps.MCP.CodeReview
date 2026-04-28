using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using REBUSS.Pure.AzureDevOps.Providers;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Shared;

namespace REBUSS.Pure.AzureDevOps.Tests;

/// <summary>
/// Regression tests for <see cref="ServiceCollectionExtensions.AddAzureDevOpsProvider"/>.
/// Catches DI-graph composition failures that would only otherwise surface in smoke
/// tests against a live Azure DevOps endpoint.
/// </summary>
public class ServiceCollectionExtensionsTests
{
    private static ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        // AddAzureDevOpsProvider depends on a couple of Core-side services that are
        // normally registered alongside it by the host (REBUSS.Pure.DependencyInjection).
        // Wire the minimum subset here so the ADO subgraph alone is composable end-to-end.
        services.AddSingleton(Substitute.For<IWorkspaceRootProvider>());
        services.AddSingleton<IFileClassifier, FileClassifier>();
        services.AddSingleton(Substitute.For<IStructuredDiffBuilder>());
        services.AddAzureDevOpsProvider(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddAzureDevOpsProvider_ResolvesAzureDevOpsDiffProvider()
    {
        // Regression guard: when AzureDevOpsDiffProvider's ctor was changed to internal
        // during the Diff/* refactor, Microsoft.Extensions.DependencyInjection's default
        // activator (which calls Type.GetConstructors() — public-only) silently failed
        // to locate a constructor. Resolution then threw at first GetService call,
        // surfacing as "Tool returned error" in the smoke tests against live Azure DevOps.
        // Registration now uses an explicit factory delegate; this test pins that contract.
        using var sp = BuildProvider();

        var provider = sp.GetRequiredService<AzureDevOpsDiffProvider>();

        Assert.NotNull(provider);
    }

    [Fact]
    public void AddAzureDevOpsProvider_ResolvesScmClientFacade_AndAllForwardedInterfaces()
    {
        // Verifies the full SCM-client facade chain — AzureDevOpsScmClient depends on
        // AzureDevOpsDiffProvider, so a regression in the diff-provider activation also
        // breaks every consumer of IScmClient / IPullRequestDataProvider /
        // IRepositoryArchiveProvider downstream.
        using var sp = BuildProvider();

        Assert.NotNull(sp.GetRequiredService<AzureDevOpsScmClient>());
        Assert.NotNull(sp.GetRequiredService<IScmClient>());
        Assert.NotNull(sp.GetRequiredService<IPullRequestDataProvider>());
        Assert.NotNull(sp.GetRequiredService<IRepositoryArchiveProvider>());
    }

    [Fact]
    public void AddAzureDevOpsProvider_ResolvesAllInternalDiffCollaborators()
    {
        // The Diff/* collaborators (DiffSkipPolicy, DiffSourcePairFactory, PrDataFetcher)
        // have public constructors but live in internal sealed classes — DI sees the
        // ctor regardless because GetConstructors returns ctors of any class accessibility.
        // This test pins that they remain resolvable through the public AddAzureDevOpsProvider
        // composition root.
        using var sp = BuildProvider();

        Assert.NotNull(sp.GetRequiredService<REBUSS.Pure.AzureDevOps.Providers.Diff.DiffSkipPolicy>());
        Assert.NotNull(sp.GetRequiredService<REBUSS.Pure.AzureDevOps.Providers.Diff.DiffSourcePairFactory>());
        Assert.NotNull(sp.GetRequiredService<REBUSS.Pure.AzureDevOps.Providers.Diff.PrDataFetcher>());
    }
}
