namespace REBUSS.Pure.SmokeTests.Infrastructure;

/// <summary>
/// xUnit fixture that manages a single <see cref="ContractMcpProcessFixture"/>
/// for Azure DevOps contract tests. All tests in the collection share one process.
/// </summary>
public sealed class AdoMcpProcessFixture : IAsyncLifetime
{
    public ContractMcpProcessFixture Server { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (!TestSettings.IsAdoConfigured)
            return;

        Server = ContractMcpProcessFixture.ForAzureDevOps(
            TestSettings.AdoPat!,
            TestSettings.AdoOrg!,
            TestSettings.AdoProject!,
            TestSettings.AdoRepo!);

        await Server.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        if (Server is not null)
            await Server.DisposeAsync();
    }
}

[CollectionDefinition("AdoContract")]
public class AdoContractCollection : ICollectionFixture<AdoMcpProcessFixture>;

/// <summary>
/// xUnit fixture that manages a single <see cref="ContractMcpProcessFixture"/>
/// for GitHub contract tests. All tests in the collection share one process.
/// </summary>
public sealed class GitHubMcpProcessFixture : IAsyncLifetime
{
    public ContractMcpProcessFixture Server { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (!TestSettings.IsGitHubConfigured)
            return;

        Server = ContractMcpProcessFixture.ForGitHub(
            TestSettings.GhPat!,
            TestSettings.GhOwner!,
            TestSettings.GhRepo!);

        await Server.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        if (Server is not null)
            await Server.DisposeAsync();
    }
}

[CollectionDefinition("GitHubContract")]
public class GitHubContractCollection : ICollectionFixture<GitHubMcpProcessFixture>;

/// <summary>
/// xUnit fixture for protocol-only tests (no credentials needed).
/// </summary>
public sealed class ProtocolMcpProcessFixture : IAsyncLifetime
{
    public ContractMcpProcessFixture Server { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Server = ContractMcpProcessFixture.ForProtocol();
        await Server.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        if (Server is not null)
            await Server.DisposeAsync();
    }
}

[CollectionDefinition("Protocol")]
public class ProtocolCollection : ICollectionFixture<ProtocolMcpProcessFixture>;
