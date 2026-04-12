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

        var logger = new CapturingLogger<GitHubChainedAuthenticationProvider>();

        var provider = new GitHubChainedAuthenticationProvider(
            Options.Create(options),
            _configStore,
            _ghCliTokenProvider,
            logger);

        await provider.GetAuthenticationAsync();

        // "Using cached GitHub token" must be logged at Debug, not Information
        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Debug && e.Message.Contains("Using cached GitHub token"));
        Assert.DoesNotContain(logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("Using cached GitHub token"));
    }

    /// <summary>
    /// Simple capturing logger that avoids the NSubstitute generic TState matching
    /// issue with <see cref="ILogger.Log{TState}"/>.
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }

        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    }
}
