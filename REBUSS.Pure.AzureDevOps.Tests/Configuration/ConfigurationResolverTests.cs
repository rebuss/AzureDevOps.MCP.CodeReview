using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using REBUSS.Pure.AzureDevOps.Configuration;
using REBUSS.Pure.Core;

namespace REBUSS.Pure.AzureDevOps.Tests.Configuration;

public class ConfigurationResolverTests
{
    private readonly IGitRemoteDetector _gitDetector = Substitute.For<IGitRemoteDetector>();
    private readonly ILocalConfigStore _configStore = Substitute.For<ILocalConfigStore>();
    private readonly IWorkspaceRootProvider _workspaceRootProvider = Substitute.For<IWorkspaceRootProvider>();

    private ConfigurationResolver CreateResolver()
    {
        return new ConfigurationResolver(
            _gitDetector,
            _configStore,
            _workspaceRootProvider,
            NullLogger<ConfigurationResolver>.Instance);
    }

    private AzureDevOpsOptions PostConfigure(AzureDevOpsOptions options)
    {
        CreateResolver().PostConfigure(null, options);
        return options;
    }

    [Fact]
    public void PostConfigure_UsesExplicitConfig_WhenAllFieldsProvided()
    {
        var options = new AzureDevOpsOptions
        {
            OrganizationName = "ExplicitOrg",
            ProjectName = "ExplicitProj",
            RepositoryName = "ExplicitRepo",
            PersonalAccessToken = "explicit-pat"
        };

        PostConfigure(options);

        Assert.Equal("ExplicitOrg", options.OrganizationName);
        Assert.Equal("ExplicitProj", options.ProjectName);
        Assert.Equal("ExplicitRepo", options.RepositoryName);
        Assert.Equal("explicit-pat", options.PersonalAccessToken);
    }

    [Fact]
    public void PostConfigure_FallsBackToGitRemote_WhenNoConfigProvided()
    {
        _gitDetector.Detect()
            .Returns(new DetectedGitInfo("DetectedOrg", "DetectedProj", "DetectedRepo"));

        var options = PostConfigure(new AzureDevOpsOptions());

        Assert.Equal("DetectedOrg", options.OrganizationName);
        Assert.Equal("DetectedProj", options.ProjectName);
        Assert.Equal("DetectedRepo", options.RepositoryName);
    }

    [Fact]
    public void PostConfigure_FallsBackToCache_WhenNoConfigAndNoGitRemote()
    {
        _gitDetector.Detect().Returns((DetectedGitInfo?)null);
        _configStore.Load().Returns(new CachedConfig
        {
            OrganizationName = "CachedOrg",
            ProjectName = "CachedProj",
            RepositoryName = "CachedRepo"
        });

        var options = PostConfigure(new AzureDevOpsOptions());

        Assert.Equal("CachedOrg", options.OrganizationName);
        Assert.Equal("CachedProj", options.ProjectName);
        Assert.Equal("CachedRepo", options.RepositoryName);
    }

    [Fact]
    public void PostConfigure_ExplicitConfigTakesPrecedence_OverDetectedAndCached()
    {
        var userOptions = new AzureDevOpsOptions
        {
            OrganizationName = "ExplicitOrg",
            ProjectName = "ExplicitProj",
            RepositoryName = "ExplicitRepo"
        };

        _gitDetector.Detect()
            .Returns(new DetectedGitInfo("DetectedOrg", "DetectedProj", "DetectedRepo"));
        _configStore.Load().Returns(new CachedConfig
        {
            OrganizationName = "CachedOrg",
            ProjectName = "CachedProj",
            RepositoryName = "CachedRepo"
        });

        PostConfigure(userOptions);

        Assert.Equal("ExplicitOrg", userOptions.OrganizationName);
        Assert.Equal("ExplicitProj", userOptions.ProjectName);
        Assert.Equal("ExplicitRepo", userOptions.RepositoryName);
    }

    [Fact]
    public void PostConfigure_DetectedTakesPrecedence_OverCached()
    {
        _configStore.Load().Returns(new CachedConfig
        {
            OrganizationName = "CachedOrg",
            ProjectName = "CachedProj",
            RepositoryName = "CachedRepo"
        });
        _gitDetector.Detect()
            .Returns(new DetectedGitInfo("DetectedOrg", "DetectedProj", "DetectedRepo"));

        var options = PostConfigure(new AzureDevOpsOptions());

        Assert.Equal("DetectedOrg", options.OrganizationName);
        Assert.Equal("DetectedProj", options.ProjectName);
        Assert.Equal("DetectedRepo", options.RepositoryName);
    }

    [Fact]
    public void PostConfigure_LeavesFieldsEmpty_WhenCannotBeResolved()
    {
        _gitDetector.Detect().Returns((DetectedGitInfo?)null);
        _configStore.Load().Returns((CachedConfig?)null);

        var options = PostConfigure(new AzureDevOpsOptions());

        Assert.Equal(string.Empty, options.OrganizationName);
        Assert.Equal(string.Empty, options.ProjectName);
        Assert.Equal(string.Empty, options.RepositoryName);
    }

    [Fact]
    public void PostConfigure_SkipsCache_WhenConfigIsIncomplete()
    {
        _gitDetector.Detect().Returns((DetectedGitInfo?)null);
        _configStore.Load().Returns((CachedConfig?)null);

        PostConfigure(new AzureDevOpsOptions());

        _configStore.DidNotReceive().Save(Arg.Any<CachedConfig>());
    }

    [Fact]
    public void PostConfigure_SavesResolvedConfig_ToCache()
    {
        _gitDetector.Detect()
            .Returns(new DetectedGitInfo("Org", "Proj", "Repo"));
        _configStore.Load().Returns((CachedConfig?)null);

        PostConfigure(new AzureDevOpsOptions());

        _configStore.Received(1).Save(Arg.Is<CachedConfig>(c =>
            c.OrganizationName == "Org" &&
            c.ProjectName == "Proj" &&
            c.RepositoryName == "Repo"));
    }

    [Fact]
    public void PostConfigure_MixesSources_WhenPartialConfigProvided()
    {
        var options = new AzureDevOpsOptions
        {
            OrganizationName = "ExplicitOrg"
        };

        _gitDetector.Detect()
            .Returns(new DetectedGitInfo("DetectedOrg", "DetectedProj", "DetectedRepo"));

        PostConfigure(options);

        Assert.Equal("ExplicitOrg", options.OrganizationName);
        Assert.Equal("DetectedProj", options.ProjectName);
        Assert.Equal("DetectedRepo", options.RepositoryName);
    }

    [Fact]
    public void PostConfigure_PreservesPat_WithoutModification()
    {
        var options = new AzureDevOpsOptions { PersonalAccessToken = "my-pat" };
        _gitDetector.Detect()
            .Returns(new DetectedGitInfo("Org", "Proj", "Repo"));

        PostConfigure(options);

        Assert.Equal("my-pat", options.PersonalAccessToken);
    }

    // --- Resolve static method tests ---

    [Theory]
    [InlineData("explicit", "cached", "detected", "explicit")]
    [InlineData("", "cached", "detected", "detected")]
    [InlineData("", "", "detected", "detected")]
    public void Resolve_ReturnsByPrecedence(string user, string cached, string detected, string expected)
    {
        var result = ConfigurationResolver.Resolve(user, cached, detected, "TestField");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Resolve_ReturnsEmpty_WhenAllSourcesEmpty()
    {
        var result = ConfigurationResolver.Resolve("", "", "", "TestField");

        Assert.Equal(string.Empty, result);
    }

    // --- Workspace root integration tests ---

    [Fact]
    public void PostConfigure_UsesWorkspaceRoot_ForGitDetection()
    {
        _workspaceRootProvider.ResolveRepositoryRoot().Returns("/workspace/repo");
        _gitDetector.Detect("/workspace/repo")
            .Returns(new DetectedGitInfo("WsOrg", "WsProj", "WsRepo"));

        var options = PostConfigure(new AzureDevOpsOptions());

        Assert.Equal("WsOrg", options.OrganizationName);
        Assert.Equal("WsProj", options.ProjectName);
        Assert.Equal("WsRepo", options.RepositoryName);
    }

    [Fact]
    public void PostConfigure_FallsBackToDefaultDetection_WhenWorkspaceRootIsNull()
    {
        _workspaceRootProvider.ResolveRepositoryRoot().Returns((string?)null);
        _gitDetector.Detect()
            .Returns(new DetectedGitInfo("DefaultOrg", "DefaultProj", "DefaultRepo"));

        var options = PostConfigure(new AzureDevOpsOptions());

        Assert.Equal("DefaultOrg", options.OrganizationName);
        Assert.Equal("DefaultProj", options.ProjectName);
        Assert.Equal("DefaultRepo", options.RepositoryName);
    }

    [Fact]
    public void PostConfigure_FallsBackToDefaultDetection_WhenWorkspaceRootDetectionFails()
    {
        _workspaceRootProvider.ResolveRepositoryRoot().Returns("/workspace/repo");
        _gitDetector.Detect("/workspace/repo").Returns((DetectedGitInfo?)null);
        _gitDetector.Detect()
            .Returns(new DetectedGitInfo("FallbackOrg", "FallbackProj", "FallbackRepo"));

        var options = PostConfigure(new AzureDevOpsOptions());

        Assert.Equal("FallbackOrg", options.OrganizationName);
        Assert.Equal("FallbackProj", options.ProjectName);
        Assert.Equal("FallbackRepo", options.RepositoryName);
    }
}
