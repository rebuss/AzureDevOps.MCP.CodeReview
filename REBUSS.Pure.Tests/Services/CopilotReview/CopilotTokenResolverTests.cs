using Microsoft.Extensions.Options;
using REBUSS.Pure.Core.Services.CopilotReview;
using REBUSS.Pure.Services.CopilotReview;

namespace REBUSS.Pure.Tests.Services.CopilotReview;

/// <summary>
/// Unit tests for <see cref="CopilotTokenResolver"/> — feature 018 T012.
/// Covers FR-009/010/011/012/013a: the env-&gt;config-&gt;logged-in-user resolution
/// order, blank/whitespace handling, and the log-safety invariant that neither
/// the resolver nor its return value expose the token value as a log field.
/// </summary>
public class CopilotTokenResolverTests : IDisposable
{
    private const string EnvVarName = "REBUSS_COPILOT_TOKEN";

    public CopilotTokenResolverTests()
    {
        // Ensure a clean env before each test.
        Environment.SetEnvironmentVariable(EnvVarName, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvVarName, null);
    }

    private static CopilotTokenResolver Create(string? configToken = null) =>
        new(Options.Create(new CopilotReviewOptions { GitHubToken = configToken }));

    [Fact]
    public void Resolve_EnvVarSet_ReturnsEnvironmentOverride()
    {
        Environment.SetEnvironmentVariable(EnvVarName, "env-token");
        var resolver = Create(configToken: "config-token");

        var (token, source) = resolver.Resolve();

        Assert.Equal("env-token", token);
        Assert.Equal(CopilotTokenSource.EnvironmentOverride, source);
    }

    [Fact]
    public void Resolve_OnlyConfigSet_ReturnsConfigOverride()
    {
        var resolver = Create(configToken: "config-token");

        var (token, source) = resolver.Resolve();

        Assert.Equal("config-token", token);
        Assert.Equal(CopilotTokenSource.ConfigOverride, source);
    }

    [Fact]
    public void Resolve_NeitherSet_ReturnsLoggedInUserWithNullToken()
    {
        var resolver = Create(configToken: null);

        var (token, source) = resolver.Resolve();

        Assert.Null(token);
        Assert.Equal(CopilotTokenSource.LoggedInUser, source);
    }

    [Fact]
    public void Resolve_EnvVarWhitespace_TreatedAsUnsetFallsThroughToConfig()
    {
        Environment.SetEnvironmentVariable(EnvVarName, "   ");
        var resolver = Create(configToken: "config-token");

        var (token, source) = resolver.Resolve();

        Assert.Equal("config-token", token);
        Assert.Equal(CopilotTokenSource.ConfigOverride, source);
    }

    [Fact]
    public void Resolve_ConfigWhitespace_TreatedAsUnsetFallsThroughToLoggedInUser()
    {
        var resolver = Create(configToken: "   ");

        var (token, source) = resolver.Resolve();

        Assert.Null(token);
        Assert.Equal(CopilotTokenSource.LoggedInUser, source);
    }

    [Fact]
    public void Resolve_BothWhitespace_ReturnsLoggedInUser()
    {
        Environment.SetEnvironmentVariable(EnvVarName, "\t\n ");
        var resolver = Create(configToken: " ");

        var (token, source) = resolver.Resolve();

        Assert.Null(token);
        Assert.Equal(CopilotTokenSource.LoggedInUser, source);
    }

    [Fact]
    public void Resolve_EnvVarEmpty_TreatedAsUnset()
    {
        Environment.SetEnvironmentVariable(EnvVarName, string.Empty);
        var resolver = Create(configToken: "config-token");

        var (token, source) = resolver.Resolve();

        Assert.Equal("config-token", token);
        Assert.Equal(CopilotTokenSource.ConfigOverride, source);
    }

    [Fact]
    public void Resolve_DoesNotMutateInput()
    {
        // Regression guard: the resolver is a pure function; it must not modify
        // the options object or the environment.
        const string originalConfig = "config-token";
        Environment.SetEnvironmentVariable(EnvVarName, "env-token");
        var options = new CopilotReviewOptions { GitHubToken = originalConfig };
        var resolver = new CopilotTokenResolver(Options.Create(options));

        _ = resolver.Resolve();

        Assert.Equal(originalConfig, options.GitHubToken);
        Assert.Equal("env-token", Environment.GetEnvironmentVariable(EnvVarName));
    }

    [Fact]
    public void Resolve_ResolverToString_DoesNotContainTokenValue()
    {
        // FR-013a log-safety: the resolver itself must never expose the token
        // value in its ToString/diagnostic surface.
        const string sentinelToken = "SENTINEL_TOKEN_VALUE_DO_NOT_LEAK";
        Environment.SetEnvironmentVariable(EnvVarName, sentinelToken);
        var resolver = Create(configToken: "also-should-not-leak");

        var resolverDump = resolver.ToString() ?? string.Empty;

        Assert.DoesNotContain(sentinelToken, resolverDump);
        Assert.DoesNotContain("also-should-not-leak", resolverDump);
    }
}
