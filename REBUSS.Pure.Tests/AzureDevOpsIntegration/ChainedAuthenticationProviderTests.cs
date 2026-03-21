using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using REBUSS.Pure.AzureDevOps.Configuration;

namespace REBUSS.Pure.Tests.AzureDevOpsIntegration;

public class ChainedAuthenticationProviderTests
{
    private readonly ILocalConfigStore _configStore = Substitute.For<ILocalConfigStore>();
    private readonly IAzureCliTokenProvider _azureCliTokenProvider = Substitute.For<IAzureCliTokenProvider>();

    private ChainedAuthenticationProvider CreateProvider(AzureDevOpsOptions options)
    {
        return new ChainedAuthenticationProvider(
            Options.Create(options),
            _configStore,
            _azureCliTokenProvider,
            NullLogger<ChainedAuthenticationProvider>.Instance);
    }

    [Fact]
    public async Task GetAuthenticationAsync_ReturnsBasicAuth_WhenPatProvided()
    {
        var options = new AzureDevOpsOptions { PersonalAccessToken = "my-pat" };
        var provider = CreateProvider(options);

        var result = await provider.GetAuthenticationAsync();

        Assert.Equal("Basic", result.Scheme);
        Assert.NotNull(result.Parameter);
    }

    [Fact]
    public async Task GetAuthenticationAsync_Pat_TakesPrecedence_OverCachedToken()
    {
        var options = new AzureDevOpsOptions { PersonalAccessToken = "my-pat" };
        _configStore.Load().Returns(new CachedConfig
        {
            AccessToken = "cached-bearer-token",
            TokenType = "Bearer",
            TokenExpiresOn = DateTime.UtcNow.AddHours(1)
        });

        var provider = CreateProvider(options);
        var result = await provider.GetAuthenticationAsync();

        Assert.Equal("Basic", result.Scheme);
    }

    [Fact]
    public async Task GetAuthenticationAsync_UsesCachedBearerToken_WhenNoPat()
    {
        var options = new AzureDevOpsOptions();
        _configStore.Load().Returns(new CachedConfig
        {
            AccessToken = "cached-bearer-token",
            TokenType = "Bearer",
            TokenExpiresOn = DateTime.UtcNow.AddHours(1)
        });

        var provider = CreateProvider(options);
        var result = await provider.GetAuthenticationAsync();

        Assert.Equal("Bearer", result.Scheme);
        Assert.Equal("cached-bearer-token", result.Parameter);
    }

    [Fact]
    public async Task GetAuthenticationAsync_UsesCachedBasicToken_WhenNoPat()
    {
        var options = new AzureDevOpsOptions();
        _configStore.Load().Returns(new CachedConfig
        {
            AccessToken = "cached-basic-token",
            TokenType = "Basic"
        });

        var provider = CreateProvider(options);
        var result = await provider.GetAuthenticationAsync();

        Assert.Equal("Basic", result.Scheme);
        Assert.Equal("cached-basic-token", result.Parameter);
    }

    [Fact]
    public async Task GetAuthenticationAsync_UsesCachedBasicToken_WhenNoExpiry()
    {
        // Basic = cached PAT: null expiry is valid (PAT expiry is managed in Azure DevOps, not here)
        var options = new AzureDevOpsOptions();
        _configStore.Load().Returns(new CachedConfig
        {
            AccessToken = "cached-pat-token",
            TokenType = "Basic",
            TokenExpiresOn = null
        });

        var provider = CreateProvider(options);
        var result = await provider.GetAuthenticationAsync();

        Assert.Equal("Basic", result.Scheme);
        Assert.Equal("cached-pat-token", result.Parameter);
    }

    [Fact]
    public async Task GetAuthenticationAsync_RefreshesViaAzCli_WhenCachedBearerTokenHasNoExpiry()
    {
        // Bearer = Azure CLI token: null expiry means old/stale cache entry, must refresh
        var options = new AzureDevOpsOptions();
        _configStore.Load().Returns(new CachedConfig
        {
            AccessToken = "old-bearer-no-expiry",
            TokenType = "Bearer",
            TokenExpiresOn = null
        });
        _azureCliTokenProvider.GetTokenAsync(Arg.Any<CancellationToken>())
            .Returns(new AzureCliToken("fresh-cli-token", DateTime.UtcNow.AddHours(1)));

        var provider = CreateProvider(options);
        var result = await provider.GetAuthenticationAsync();

        Assert.Equal("Bearer", result.Scheme);
        Assert.Equal("fresh-cli-token", result.Parameter);
    }

    [Fact]
    public async Task GetAuthenticationAsync_ThrowsWithAuthInstructions_WhenNoPatAndNoCachedTokenAndNoAzCli()
    {
        var options = new AzureDevOpsOptions();
        _azureCliTokenProvider.GetTokenAsync(Arg.Any<CancellationToken>()).Returns((AzureCliToken?)null);
        var provider = CreateProvider(options);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetAuthenticationAsync());

        Assert.Contains("az login", ex.Message);
        Assert.Contains("PersonalAccessToken", ex.Message);
    }

    [Fact]
    public async Task GetAuthenticationAsync_ThrowsWithAuthInstructions_WhenCachedTokenExpiredAndNoAzCli()
    {
        var options = new AzureDevOpsOptions();
        _configStore.Load().Returns(new CachedConfig
        {
            AccessToken = "expired-token",
            TokenType = "Bearer",
            TokenExpiresOn = DateTime.UtcNow.AddHours(-1)
        });
        _azureCliTokenProvider.GetTokenAsync(Arg.Any<CancellationToken>()).Returns((AzureCliToken?)null);

        var provider = CreateProvider(options);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetAuthenticationAsync());

        Assert.Contains("az login", ex.Message);
        Assert.Contains("PersonalAccessToken", ex.Message);
    }

    [Fact]
    public async Task GetAuthenticationAsync_UsesAzureCliToken_WhenNoPatAndNoCachedToken()
    {
        var options = new AzureDevOpsOptions();
        _azureCliTokenProvider.GetTokenAsync(Arg.Any<CancellationToken>())
            .Returns(new AzureCliToken("cli-bearer-token", DateTime.UtcNow.AddHours(1)));

        var provider = CreateProvider(options);
        var result = await provider.GetAuthenticationAsync();

        Assert.Equal("Bearer", result.Scheme);
        Assert.Equal("cli-bearer-token", result.Parameter);
    }

    [Fact]
    public async Task GetAuthenticationAsync_CachesAzureCliToken()
    {
        var options = new AzureDevOpsOptions();
        var expiresOn = DateTime.UtcNow.AddHours(1);
        _azureCliTokenProvider.GetTokenAsync(Arg.Any<CancellationToken>())
            .Returns(new AzureCliToken("cli-token", expiresOn));

        var provider = CreateProvider(options);
        await provider.GetAuthenticationAsync();

        _configStore.Received(1).Save(Arg.Is<CachedConfig>(c =>
            c.AccessToken == "cli-token" &&
            c.TokenType == "Bearer" &&
            c.TokenExpiresOn == expiresOn));
    }

    [Fact]
    public async Task GetAuthenticationAsync_UsesAzureCliToken_WhenCachedTokenExpired()
    {
        var options = new AzureDevOpsOptions();
        _configStore.Load().Returns(new CachedConfig
        {
            AccessToken = "expired-token",
            TokenType = "Bearer",
            TokenExpiresOn = DateTime.UtcNow.AddHours(-1)
        });
        _azureCliTokenProvider.GetTokenAsync(Arg.Any<CancellationToken>())
            .Returns(new AzureCliToken("fresh-cli-token", DateTime.UtcNow.AddHours(1)));

        var provider = CreateProvider(options);
        var result = await provider.GetAuthenticationAsync();

        Assert.Equal("Bearer", result.Scheme);
        Assert.Equal("fresh-cli-token", result.Parameter);
    }

    [Fact]
    public void InvalidateCachedToken_ClearsTokenFields()
    {
        var options = new AzureDevOpsOptions();
        _configStore.Load().Returns(new CachedConfig
        {
            AccessToken = "some-token",
            TokenType = "Bearer",
            TokenExpiresOn = DateTime.UtcNow.AddHours(1)
        });

        var provider = CreateProvider(options);
        provider.InvalidateCachedToken();

        _configStore.Received(1).Save(Arg.Is<CachedConfig>(c =>
            c.AccessToken == null &&
            c.TokenType == null &&
            c.TokenExpiresOn == null));
    }

    [Fact]
    public void InvalidateCachedToken_DoesNotThrow_WhenNoCachedConfig()
    {
        var options = new AzureDevOpsOptions();
        _configStore.Load().Returns((CachedConfig?)null);

        var provider = CreateProvider(options);
        var ex = Record.Exception(() => provider.InvalidateCachedToken());

        Assert.Null(ex);
        _configStore.DidNotReceive().Save(Arg.Any<CachedConfig>());
    }

    [Fact]
    public void BuildAuthRequiredMessage_ContainsAzLoginAndPatInstructions()
    {
        var message = ChainedAuthenticationProvider.BuildAuthRequiredMessage();

        Assert.Contains("az login", message);
        Assert.Contains("appsettings.Local.json", message);
        Assert.Contains("PersonalAccessToken", message);
        Assert.Contains("dev.azure.com", message);
        Assert.Contains("Code (Read)", message);
    }
}
