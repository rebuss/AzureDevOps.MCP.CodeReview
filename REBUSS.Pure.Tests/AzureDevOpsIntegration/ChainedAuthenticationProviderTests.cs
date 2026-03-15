using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using REBUSS.Pure.AzureDevOpsIntegration.Configuration;

namespace REBUSS.Pure.Tests.AzureDevOpsIntegration;

public class ChainedAuthenticationProviderTests
{
    private readonly ILocalConfigStore _configStore = Substitute.For<ILocalConfigStore>();

    private ChainedAuthenticationProvider CreateProvider(AzureDevOpsOptions options)
    {
        return new ChainedAuthenticationProvider(
            Options.Create(options),
            _configStore,
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
    public async Task GetAuthenticationAsync_UsesCachedToken_WhenNoExpiry()
    {
        var options = new AzureDevOpsOptions();
        _configStore.Load().Returns(new CachedConfig
        {
            AccessToken = "forever-token",
            TokenType = "Bearer",
            TokenExpiresOn = null
        });

        var provider = CreateProvider(options);
        var result = await provider.GetAuthenticationAsync();

        Assert.Equal("Bearer", result.Scheme);
        Assert.Equal("forever-token", result.Parameter);
    }

    [Fact]
    public async Task GetAuthenticationAsync_ThrowsWithPatInstructions_WhenNoPatAndNoCachedToken()
    {
        var options = new AzureDevOpsOptions();
        var provider = CreateProvider(options);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetAuthenticationAsync());

        Assert.Contains("PersonalAccessToken", ex.Message);
        Assert.Contains("appsettings.Local.json", ex.Message);
        Assert.Contains("dev.azure.com", ex.Message);
    }

    [Fact]
    public async Task GetAuthenticationAsync_ThrowsWithPatInstructions_WhenCachedTokenExpired()
    {
        var options = new AzureDevOpsOptions();
        _configStore.Load().Returns(new CachedConfig
        {
            AccessToken = "expired-token",
            TokenType = "Bearer",
            TokenExpiresOn = DateTime.UtcNow.AddHours(-1)
        });

        var provider = CreateProvider(options);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetAuthenticationAsync());

        Assert.Contains("PersonalAccessToken", ex.Message);
        Assert.Contains("appsettings.Local.json", ex.Message);
    }

    [Fact]
    public void BuildPatRequiredMessage_ContainsActionableInstructions()
    {
        var message = ChainedAuthenticationProvider.BuildPatRequiredMessage();

        Assert.Contains("appsettings.Local.json", message);
        Assert.Contains("PersonalAccessToken", message);
        Assert.Contains("dev.azure.com", message);
        Assert.Contains("Code (Read)", message);
    }
}
