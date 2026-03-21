using REBUSS.Pure.AzureDevOps.Configuration;

namespace REBUSS.Pure.AzureDevOps.Tests.Configuration;

public class GitRemoteDetectorTests
{
    // ---- HTTPS URLs ----

    [Fact]
    public void ParseRemoteUrl_Parses_StandardHttpsUrl()
    {
        var result = GitRemoteDetector.ParseRemoteUrl(
            "https://dev.azure.com/MyOrg/MyProject/_git/MyRepo");

        Assert.NotNull(result);
        Assert.Equal("MyOrg", result.OrganizationName);
        Assert.Equal("MyProject", result.ProjectName);
        Assert.Equal("MyRepo", result.RepositoryName);
    }

    [Fact]
    public void ParseRemoteUrl_Parses_HttpsUrlWithUser()
    {
        var result = GitRemoteDetector.ParseRemoteUrl(
            "https://MyOrg@dev.azure.com/MyOrg/MyProject/_git/MyRepo");

        Assert.NotNull(result);
        Assert.Equal("MyOrg", result.OrganizationName);
        Assert.Equal("MyProject", result.ProjectName);
        Assert.Equal("MyRepo", result.RepositoryName);
    }

    // ---- SSH URLs ----

    [Fact]
    public void ParseRemoteUrl_Parses_SshUrl()
    {
        var result = GitRemoteDetector.ParseRemoteUrl(
            "git@ssh.dev.azure.com:v3/MyOrg/MyProject/MyRepo");

        Assert.NotNull(result);
        Assert.Equal("MyOrg", result.OrganizationName);
        Assert.Equal("MyProject", result.ProjectName);
        Assert.Equal("MyRepo", result.RepositoryName);
    }

    // ---- Non-Azure DevOps URLs ----

    [Fact]
    public void ParseRemoteUrl_ReturnsNull_ForGitHubUrl()
    {
        var result = GitRemoteDetector.ParseRemoteUrl(
            "https://github.com/rebuss/CodeReview.MCP");

        Assert.Null(result);
    }

    [Fact]
    public void ParseRemoteUrl_ReturnsNull_ForEmptyString()
    {
        var result = GitRemoteDetector.ParseRemoteUrl(string.Empty);

        Assert.Null(result);
    }

    [Fact]
    public void ParseRemoteUrl_ReturnsNull_ForMalformedUrl()
    {
        var result = GitRemoteDetector.ParseRemoteUrl("not-a-url");

        Assert.Null(result);
    }

    // ---- Edge cases ----

    [Fact]
    public void ParseRemoteUrl_HandlesTrailingWhitespace()
    {
        var result = GitRemoteDetector.ParseRemoteUrl(
            "https://dev.azure.com/Org/Proj/_git/Repo  ");

        Assert.NotNull(result);
        Assert.Equal("Repo", result.RepositoryName);
    }

    [Fact]
    public void ParseRemoteUrl_Parses_UrlWithQueryString()
    {
        var result = GitRemoteDetector.ParseRemoteUrl(
            "https://dev.azure.com/Org/Proj/_git/Repo?param=value");

        Assert.NotNull(result);
        Assert.Equal("Repo", result.RepositoryName);
    }

    // ---- FindGitRepositoryRoot ----

    [Fact]
    public void FindGitRepositoryRoot_FindsRoot_WhenStartingFromSubdirectory()
    {
        // The test project itself is inside a Git repository —
        // AppContext.BaseDirectory is <repo>/REBUSS.Pure.Tests/bin/Debug/net8.0/
        var root = GitRemoteDetector.FindGitRepositoryRoot(AppContext.BaseDirectory);

        Assert.NotNull(root);
        Assert.True(Directory.Exists(Path.Combine(root, ".git")),
            $"Expected .git directory at {root}");
    }

    [Fact]
    public void FindGitRepositoryRoot_ReturnsNull_WhenNoGitDirectory()
    {
        // Use the filesystem root where no .git directory exists.
        // On Linux Environment.SystemDirectory can be empty, so fall back to "/".
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
        var searchPath = string.IsNullOrEmpty(systemRoot) ? "/" : systemRoot;

        var root = GitRemoteDetector.FindGitRepositoryRoot(searchPath);

        Assert.Null(root);
    }

    // ---- GetCandidateDirectories ----

    [Fact]
    public void GetCandidateDirectories_IncludesCurrentDirectory()
    {
        var candidates = GitRemoteDetector.GetCandidateDirectories();

        Assert.Contains(candidates, c =>
            string.Equals(c, Environment.CurrentDirectory, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetCandidateDirectories_IncludesGitRepoRoot_FromExecutableLocation()
    {
        var candidates = GitRemoteDetector.GetCandidateDirectories();

        // The test binary sits inside a Git repo, so the repo root should be found
        var repoRoot = GitRemoteDetector.FindGitRepositoryRoot(AppContext.BaseDirectory);
        if (repoRoot is not null)
        {
            Assert.Contains(candidates, c =>
                string.Equals(c, repoRoot, StringComparison.OrdinalIgnoreCase));
        }
    }
}
