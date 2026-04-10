using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using REBUSS.Pure.GitHub.Configuration;
using REBUSS.Pure.GitHub.Properties;

namespace REBUSS.Pure.GitHub.Tests.Configuration;

public class GitHubChainedAuthenticationProviderTests
{
    private readonly IGitHubConfigStore _configStore = Substitute.For<IGitHubConfigStore>();
    private readonly IGitHubCliTokenProvider _ghCliTokenProvider = Substitute.For<IGitHubCliTokenProvider>();

    private GitHubChainedAuthenticationProvider CreateProvider(GitHubOptions options)
    {
        return new GitHubChainedAuthenticationProvider(
            Options.Create(options),
            _configStore,
            _ghCliTokenProvider,
            NullLogger<GitHubChainedAuthenticationProvider>.Instance);
    }

    [Fact]
    public async Task GetAuthenticationAsync_ReturnsBearerAuth_WhenPatProvided()
    {
        var options = new GitHubOptions { PersonalAccessToken = "ghp_my-pat" };
        var provider = CreateProvider(options);

        var result = await provider.GetAuthenticationAsync();

        Assert.Equal("Bearer", result.Scheme);
        Assert.Equal("ghp_my-pat", result.Parameter);
    }

    [Fact]
    public async Task GetAuthenticationAsync_Pat_TakesPrecedence_OverCachedToken()
    {
        var options = new GitHubOptions { PersonalAccessToken = "ghp_my-pat" };
        _configStore.Load().Returns(new GitHubCachedConfig
        {
            AccessToken = "cached-token",
            TokenExpiresOn = DateTime.UtcNow.AddHours(1)
        });

        var provider = CreateProvider(options);
        var result = await provider.GetAuthenticationAsync();

        Assert.Equal("Bearer", result.Scheme);
        Assert.Equal("ghp_my-pat", result.Parameter);
    }

    [Fact]
    public async Task GetAuthenticationAsync_UsesCachedToken_WhenNoPat()
    {
        var options = new GitHubOptions();
        _configStore.Load().Returns(new GitHubCachedConfig
        {
            AccessToken = "cached-bearer-token",
            TokenExpiresOn = DateTime.UtcNow.AddHours(1)
        });

        var provider = CreateProvider(options);
        var result = await provider.GetAuthenticationAsync();

        Assert.Equal("Bearer", result.Scheme);
        Assert.Equal("cached-bearer-token", result.Parameter);
    }

    [Fact]
    public async Task GetAuthenticationAsync_UsesCachedToken_WhenNoExpiry()
    {
        // No expiry means valid (GitHub tokens don't have built-in expiry)
        var options = new GitHubOptions();
        _configStore.Load().Returns(new GitHubCachedConfig
        {
            AccessToken = "cached-token-no-expiry",
            TokenExpiresOn = null
        });

        var provider = CreateProvider(options);
        var result = await provider.GetAuthenticationAsync();

        Assert.Equal("Bearer", result.Scheme);
        Assert.Equal("cached-token-no-expiry", result.Parameter);
    }

    [Fact]
    public async Task GetAuthenticationAsync_UsesGhCliToken_WhenCachedTokenExpired()
    {
        var options = new GitHubOptions();
        _configStore.Load().Returns(new GitHubCachedConfig
        {
            AccessToken = "expired-token",
            TokenExpiresOn = DateTime.UtcNow.AddHours(-1)
        });
        _ghCliTokenProvider.GetTokenAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubCliToken("fresh-cli-token", DateTime.UtcNow.AddDays(365)));

        var provider = CreateProvider(options);
        var result = await provider.GetAuthenticationAsync();

        Assert.Equal("Bearer", result.Scheme);
        Assert.Equal("fresh-cli-token", result.Parameter);
    }

    [Fact]
    public async Task GetAuthenticationAsync_UsesGhCliToken_WhenNoPatAndNoCachedToken()
    {
        var options = new GitHubOptions();
        _ghCliTokenProvider.GetTokenAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubCliToken("cli-token", DateTime.UtcNow.AddDays(365)));

        var provider = CreateProvider(options);
        var result = await provider.GetAuthenticationAsync();

        Assert.Equal("Bearer", result.Scheme);
        Assert.Equal("cli-token", result.Parameter);
    }

    [Fact]
    public async Task GetAuthenticationAsync_CachesGhCliToken()
    {
        var options = new GitHubOptions();
        var expiresOn = DateTime.UtcNow.AddDays(365);
        _ghCliTokenProvider.GetTokenAsync(Arg.Any<CancellationToken>())
            .Returns(new GitHubCliToken("cli-token", expiresOn));

        var provider = CreateProvider(options);
        await provider.GetAuthenticationAsync();

        _configStore.Received(1).Save(Arg.Is<GitHubCachedConfig>(c =>
            c.AccessToken == "cli-token" &&
            c.TokenExpiresOn == expiresOn));
    }

    [Fact]
    public async Task GetAuthenticationAsync_ThrowsWithAuthInstructions_WhenNoPatNoCacheNoGhCli()
    {
        var options = new GitHubOptions();
        _ghCliTokenProvider.GetTokenAsync(Arg.Any<CancellationToken>()).Returns((GitHubCliToken?)null);
        var provider = CreateProvider(options);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetAuthenticationAsync());

        Assert.Contains("gh auth login", ex.Message);
        Assert.Contains("PersonalAccessToken", ex.Message);
    }

    [Fact]
    public async Task GetAuthenticationAsync_ThrowsWithAuthInstructions_WhenCachedTokenExpiredAndNoGhCli()
    {
        var options = new GitHubOptions();
        _configStore.Load().Returns(new GitHubCachedConfig
        {
            AccessToken = "expired-token",
            TokenExpiresOn = DateTime.UtcNow.AddHours(-1)
        });
        _ghCliTokenProvider.GetTokenAsync(Arg.Any<CancellationToken>()).Returns((GitHubCliToken?)null);

        var provider = CreateProvider(options);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetAuthenticationAsync());

        Assert.Contains("gh auth login", ex.Message);
        Assert.Contains("PersonalAccessToken", ex.Message);
    }

    [Fact]
    public void InvalidateCachedToken_ClearsAccessTokenAndExpiry()
    {
        var cached = new GitHubCachedConfig
        {
            Owner = "my-org",
            RepositoryName = "my-repo",
            AccessToken = "token-to-clear",
            TokenExpiresOn = DateTime.UtcNow.AddHours(1)
        };
        _configStore.Load().Returns(cached);

        var provider = CreateProvider(new GitHubOptions());
        provider.InvalidateCachedToken();

        _configStore.Received(1).Save(Arg.Is<GitHubCachedConfig>(c =>
            c.AccessToken == null &&
            c.TokenExpiresOn == null &&
            c.Owner == "my-org" &&
            c.RepositoryName == "my-repo"));
    }

    [Fact]
    public void InvalidateCachedToken_DoesNotThrow_WhenNoCachedConfig()
    {
        _configStore.Load().Returns((GitHubCachedConfig?)null);

        var provider = CreateProvider(new GitHubOptions());

        var ex = Record.Exception(() => provider.InvalidateCachedToken());
        Assert.Null(ex);
    }

    [Fact]
    public void BuildAuthRequiredMessage_ContainsGhAuthLogin()
    {
        var message = Resources.ErrorGitHubAuthRequired;

        Assert.Contains("gh auth login", message);
    }

    [Fact]
    public void BuildAuthRequiredMessage_ContainsPatInstructions()
    {
        var message = Resources.ErrorGitHubAuthRequired;

        Assert.Contains("PersonalAccessToken", message);
        Assert.Contains("github.com/settings/tokens", message);
    }

    [Fact]
    public void BuildAuthRequiredMessage_ContainsRebussPureInit()
    {
        var message = Resources.ErrorGitHubAuthRequired;

        Assert.Contains("rebuss-pure init", message);
    }

    [Fact]
    public async Task GetAuthenticationAsync_CachedToken_LogsAtDebugLevel_NotInformation()
    {
        var options = new GitHubOptions();
        _configStore.Load().Returns(new GitHubCachedConfig
        {
            AccessToken = "cached-token",
            TokenExpiresOn = DateTime.UtcNow.AddHours(1)
        });

        var logger = Substitute.For<ILogger<GitHubChainedAuthenticationProvider>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);

        var provider = new GitHubChainedAuthenticationProvider(
            Options.Create(options),
            _configStore,
            _ghCliTokenProvider,
            logger);

        await provider.GetAuthenticationAsync();

        // "Using cached GitHub token" must be logged at Debug, not Information
        logger.Received().Log(
            LogLevel.Debug,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Using cached GitHub token")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        logger.DidNotReceive().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Using cached GitHub token")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}
