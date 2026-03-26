using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using REBUSS.Pure.Core;
using REBUSS.Pure.GitHub.Configuration;

namespace REBUSS.Pure.GitHub.Tests.Configuration;

public class GitHubConfigurationResolverTests
{
    private readonly IGitHubRemoteDetector _gitDetector = Substitute.For<IGitHubRemoteDetector>();
    private readonly IGitHubConfigStore _configStore = Substitute.For<IGitHubConfigStore>();
    private readonly IWorkspaceRootProvider _workspaceRootProvider = Substitute.For<IWorkspaceRootProvider>();

    private GitHubConfigurationResolver CreateResolver()
    {
        return new GitHubConfigurationResolver(
            _gitDetector,
            _configStore,
            _workspaceRootProvider,
            NullLogger<GitHubConfigurationResolver>.Instance);
    }

    private GitHubOptions PostConfigure(GitHubOptions options)
    {
        CreateResolver().PostConfigure(null, options);
        return options;
    }

    [Fact]
    public void PostConfigure_UsesExplicitConfig_WhenAllFieldsProvided()
    {
        var options = new GitHubOptions
        {
            Owner = "ExplicitOwner",
            RepositoryName = "ExplicitRepo",
            PersonalAccessToken = "explicit-pat"
        };

        PostConfigure(options);

        Assert.Equal("ExplicitOwner", options.Owner);
        Assert.Equal("ExplicitRepo", options.RepositoryName);
        Assert.Equal("explicit-pat", options.PersonalAccessToken);
    }

    [Fact]
    public void PostConfigure_FallsBackToGitRemote_WhenNoConfigProvided()
    {
        _gitDetector.Detect()
            .Returns(new DetectedGitHubInfo("DetectedOwner", "DetectedRepo"));

        var options = PostConfigure(new GitHubOptions());

        Assert.Equal("DetectedOwner", options.Owner);
        Assert.Equal("DetectedRepo", options.RepositoryName);
    }

    [Fact]
    public void PostConfigure_FallsBackToCache_WhenNoConfigAndNoGitRemote()
    {
        _gitDetector.Detect().Returns((DetectedGitHubInfo?)null);
        _configStore.Load().Returns(new GitHubCachedConfig
        {
            Owner = "CachedOwner",
            RepositoryName = "CachedRepo"
        });

        var options = PostConfigure(new GitHubOptions());

        Assert.Equal("CachedOwner", options.Owner);
        Assert.Equal("CachedRepo", options.RepositoryName);
    }

    [Fact]
    public void PostConfigure_ExplicitConfigTakesPrecedence_OverDetectedAndCached()
    {
        var userOptions = new GitHubOptions
        {
            Owner = "ExplicitOwner",
            RepositoryName = "ExplicitRepo"
        };

        _gitDetector.Detect()
            .Returns(new DetectedGitHubInfo("DetectedOwner", "DetectedRepo"));
        _configStore.Load().Returns(new GitHubCachedConfig
        {
            Owner = "CachedOwner",
            RepositoryName = "CachedRepo"
        });

        PostConfigure(userOptions);

        Assert.Equal("ExplicitOwner", userOptions.Owner);
        Assert.Equal("ExplicitRepo", userOptions.RepositoryName);
    }

    [Fact]
    public void PostConfigure_DetectedTakesPrecedence_OverCached()
    {
        _configStore.Load().Returns(new GitHubCachedConfig
        {
            Owner = "CachedOwner",
            RepositoryName = "CachedRepo"
        });
        _gitDetector.Detect()
            .Returns(new DetectedGitHubInfo("DetectedOwner", "DetectedRepo"));

        var options = PostConfigure(new GitHubOptions());

        Assert.Equal("DetectedOwner", options.Owner);
        Assert.Equal("DetectedRepo", options.RepositoryName);
    }

    [Fact]
    public void PostConfigure_LeavesFieldsEmpty_WhenCannotBeResolved()
    {
        _gitDetector.Detect().Returns((DetectedGitHubInfo?)null);
        _configStore.Load().Returns((GitHubCachedConfig?)null);

        var options = PostConfigure(new GitHubOptions());

        Assert.Equal(string.Empty, options.Owner);
        Assert.Equal(string.Empty, options.RepositoryName);
    }

    [Fact]
    public void PostConfigure_SkipsCache_WhenConfigIsIncomplete()
    {
        _gitDetector.Detect().Returns((DetectedGitHubInfo?)null);
        _configStore.Load().Returns((GitHubCachedConfig?)null);

        PostConfigure(new GitHubOptions());

        _configStore.DidNotReceive().Save(Arg.Any<GitHubCachedConfig>());
    }

    [Fact]
    public void PostConfigure_SavesResolvedConfig_ToCache()
    {
        _gitDetector.Detect()
            .Returns(new DetectedGitHubInfo("Owner", "Repo"));
        _configStore.Load().Returns((GitHubCachedConfig?)null);

        PostConfigure(new GitHubOptions());

        _configStore.Received(1).Save(Arg.Is<GitHubCachedConfig>(c =>
            c.Owner == "Owner" &&
            c.RepositoryName == "Repo"));
    }

    [Fact]
    public void PostConfigure_MixesSources_WhenPartialConfigProvided()
    {
        var options = new GitHubOptions
        {
            Owner = "ExplicitOwner"
        };

        _gitDetector.Detect()
            .Returns(new DetectedGitHubInfo("DetectedOwner", "DetectedRepo"));

        PostConfigure(options);

        Assert.Equal("ExplicitOwner", options.Owner);
        Assert.Equal("DetectedRepo", options.RepositoryName);
    }

    [Fact]
    public void PostConfigure_PreservesPat_WithoutModification()
    {
        var options = new GitHubOptions { PersonalAccessToken = "my-pat" };
        _gitDetector.Detect()
            .Returns(new DetectedGitHubInfo("Owner", "Repo"));

        PostConfigure(options);

        Assert.Equal("my-pat", options.PersonalAccessToken);
    }

    // --- Resolve static method tests ---

    [Theory]
    [InlineData("explicit", "cached", "detected", "explicit")]
    [InlineData("", "cached", "detected", "detected")]
    [InlineData("", "", "detected", "detected")]
    [InlineData("", "cached", "", "cached")]
    public void Resolve_ReturnsByPrecedence(string user, string cached, string detected, string expected)
    {
        var result = GitHubConfigurationResolver.Resolve(user, cached, detected, "TestField");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Resolve_ReturnsEmpty_WhenAllSourcesEmpty()
    {
        var result = GitHubConfigurationResolver.Resolve("", "", "", "TestField");

        Assert.Equal(string.Empty, result);
    }

    // --- Workspace root integration tests ---

    [Fact]
    public void PostConfigure_UsesWorkspaceRoot_ForGitDetection()
    {
        _workspaceRootProvider.ResolveRepositoryRoot().Returns("/workspace/repo");
        _gitDetector.Detect("/workspace/repo")
            .Returns(new DetectedGitHubInfo("WsOwner", "WsRepo"));

        var options = PostConfigure(new GitHubOptions());

        Assert.Equal("WsOwner", options.Owner);
        Assert.Equal("WsRepo", options.RepositoryName);
    }

    [Fact]
    public void PostConfigure_FallsBackToDefaultDetection_WhenWorkspaceRootIsNull()
    {
        _workspaceRootProvider.ResolveRepositoryRoot().Returns((string?)null);
        _gitDetector.Detect()
            .Returns(new DetectedGitHubInfo("DefaultOwner", "DefaultRepo"));

        var options = PostConfigure(new GitHubOptions());

        Assert.Equal("DefaultOwner", options.Owner);
        Assert.Equal("DefaultRepo", options.RepositoryName);
    }

    [Fact]
    public void PostConfigure_FallsBackToDefaultDetection_WhenWorkspaceRootDetectionFails()
    {
        _workspaceRootProvider.ResolveRepositoryRoot().Returns("/workspace/repo");
        _gitDetector.Detect("/workspace/repo").Returns((DetectedGitHubInfo?)null);
        _gitDetector.Detect()
            .Returns(new DetectedGitHubInfo("FallbackOwner", "FallbackRepo"));

        var options = PostConfigure(new GitHubOptions());

        Assert.Equal("FallbackOwner", options.Owner);
        Assert.Equal("FallbackRepo", options.RepositoryName);
    }
}
