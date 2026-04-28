using REBUSS.Pure.ProviderDetection;
using AzureDevOpsNames = REBUSS.Pure.AzureDevOps.Names;
using GitHubNames = REBUSS.Pure.GitHub.Names;

namespace REBUSS.Pure.Tests.ProviderDetection;

/// <summary>
/// Characterization tests for <see cref="ProviderDetector"/> URL→provider mapping.
/// Pinned in Step 1 of the InitCommand refactor to lock the behavior previously
/// implemented (and duplicated) in <c>InitCommand.DetectProviderFromGitRemote</c>.
/// </summary>
public class ProviderDetectorTests
{
    [Theory]
    [InlineData("https://github.com/owner/repo.git")]
    [InlineData("git@github.com:owner/repo.git")]
    [InlineData("HTTPS://GITHUB.COM/Owner/Repo")]
    public void MapRemoteUrlToProvider_GitHubUrl_ReturnsGitHub(string url)
    {
        Assert.Equal(GitHubNames.Provider, ProviderDetector.MapRemoteUrlToProvider(url));
    }

    [Theory]
    [InlineData("https://dev.azure.com/org/project/_git/repo")]
    [InlineData("https://org.visualstudio.com/project/_git/repo")]
    [InlineData("git@ssh.dev.azure.com:v3/org/project/repo")]
    public void MapRemoteUrlToProvider_AzureDevOpsUrl_ReturnsAzureDevOps(string url)
    {
        Assert.Equal(AzureDevOpsNames.Provider, ProviderDetector.MapRemoteUrlToProvider(url));
    }

    [Fact]
    public void MapRemoteUrlToProvider_NullUrl_FallsBackToAzureDevOps()
    {
        Assert.Equal(AzureDevOpsNames.Provider, ProviderDetector.MapRemoteUrlToProvider(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://bitbucket.org/owner/repo.git")]
    [InlineData("https://gitlab.com/owner/repo.git")]
    public void MapRemoteUrlToProvider_UnknownDomain_FallsBackToAzureDevOps(string url)
    {
        // Preserves pre-refactor InitCommand.DetectProviderFromGitRemote behavior:
        // any non-GitHub URL was implicitly treated as Azure DevOps.
        Assert.Equal(AzureDevOpsNames.Provider, ProviderDetector.MapRemoteUrlToProvider(url));
    }

    [Fact]
    public void DetectFromGitRemote_NonGitDirectory_FallsBackToAzureDevOps()
    {
        // A freshly created temp directory has no git remote — the underlying
        // `git remote get-url origin` will fail, GetGitRemoteUrl returns null,
        // and the mapper must fall back to AzureDevOps.
        var tempDir = Path.Combine(Path.GetTempPath(), "rebuss-pure-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            Assert.Equal(AzureDevOpsNames.Provider, ProviderDetector.DetectFromGitRemote(tempDir));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
