using REBUSS.Pure.GitHub.Configuration;

namespace REBUSS.Pure.GitHub.Tests.Configuration;

public class GitHubRemoteDetectorTests
{
    // ---- HTTPS URLs ----

    [Fact]
    public void ParseRemoteUrl_Parses_StandardHttpsUrl()
    {
        var result = GitHubRemoteDetector.ParseRemoteUrl(
            "https://github.com/octocat/hello-world");

        Assert.NotNull(result);
        Assert.Equal("octocat", result.Owner);
        Assert.Equal("hello-world", result.RepositoryName);
    }

    [Fact]
    public void ParseRemoteUrl_Parses_HttpsUrlWithDotGit()
    {
        var result = GitHubRemoteDetector.ParseRemoteUrl(
            "https://github.com/octocat/hello-world.git");

        Assert.NotNull(result);
        Assert.Equal("octocat", result.Owner);
        Assert.Equal("hello-world", result.RepositoryName);
    }

    [Fact]
    public void ParseRemoteUrl_Parses_HttpsUrlWithTrailingSlash()
    {
        var result = GitHubRemoteDetector.ParseRemoteUrl(
            "https://github.com/rebuss/CodeReview/");

        Assert.NotNull(result);
        Assert.Equal("rebuss", result.Owner);
        Assert.Equal("CodeReview", result.RepositoryName);
    }

    [Fact]
    public void ParseRemoteUrl_Parses_HttpsUrlWithDotsInRepoName()
    {
        var result = GitHubRemoteDetector.ParseRemoteUrl(
            "https://github.com/rebuss/CodeReview.MCP");

        Assert.NotNull(result);
        Assert.Equal("rebuss", result.Owner);
        Assert.Equal("CodeReview.MCP", result.RepositoryName);
    }

    [Fact]
    public void ParseRemoteUrl_Parses_HttpsUrlWithDotsInRepoNameAndDotGit()
    {
        var result = GitHubRemoteDetector.ParseRemoteUrl(
            "https://github.com/rebuss/CodeReview.MCP.git");

        Assert.NotNull(result);
        Assert.Equal("rebuss", result.Owner);
        Assert.Equal("CodeReview.MCP", result.RepositoryName);
    }

    // ---- SSH URLs ----

    [Fact]
    public void ParseRemoteUrl_Parses_SshUrl()
    {
        var result = GitHubRemoteDetector.ParseRemoteUrl(
            "git@github.com:octocat/hello-world.git");

        Assert.NotNull(result);
        Assert.Equal("octocat", result.Owner);
        Assert.Equal("hello-world", result.RepositoryName);
    }

    [Fact]
    public void ParseRemoteUrl_Parses_SshUrlWithoutDotGit()
    {
        var result = GitHubRemoteDetector.ParseRemoteUrl(
            "git@github.com:rebuss/CodeReview");

        Assert.NotNull(result);
        Assert.Equal("rebuss", result.Owner);
        Assert.Equal("CodeReview", result.RepositoryName);
    }

    [Fact]
    public void ParseRemoteUrl_Parses_SshUrlWithDotsInRepoName()
    {
        var result = GitHubRemoteDetector.ParseRemoteUrl(
            "git@github.com:rebuss/CodeReview.MCP");

        Assert.NotNull(result);
        Assert.Equal("rebuss", result.Owner);
        Assert.Equal("CodeReview.MCP", result.RepositoryName);
    }

    [Fact]
    public void ParseRemoteUrl_Parses_SshUrlWithDotsInRepoNameAndDotGit()
    {
        var result = GitHubRemoteDetector.ParseRemoteUrl(
            "git@github.com:rebuss/CodeReview.MCP.git");

        Assert.NotNull(result);
        Assert.Equal("rebuss", result.Owner);
        Assert.Equal("CodeReview.MCP", result.RepositoryName);
    }

    // ---- Non-GitHub URLs ----

    [Fact]
    public void ParseRemoteUrl_ReturnsNull_ForAzureDevOpsUrl()
    {
        var result = GitHubRemoteDetector.ParseRemoteUrl(
            "https://dev.azure.com/MyOrg/MyProject/_git/MyRepo");

        Assert.Null(result);
    }

    [Fact]
    public void ParseRemoteUrl_ReturnsNull_ForGitLabUrl()
    {
        var result = GitHubRemoteDetector.ParseRemoteUrl(
            "https://gitlab.com/user/project.git");

        Assert.Null(result);
    }

    [Fact]
    public void ParseRemoteUrl_ReturnsNull_ForEmptyString()
    {
        var result = GitHubRemoteDetector.ParseRemoteUrl(string.Empty);

        Assert.Null(result);
    }

    [Fact]
    public void ParseRemoteUrl_ReturnsNull_ForMalformedUrl()
    {
        var result = GitHubRemoteDetector.ParseRemoteUrl("not-a-url");

        Assert.Null(result);
    }

    // ---- FindGitRepositoryRoot ----

    [Fact]
    public void FindGitRepositoryRoot_FindsRoot_WhenStartingFromSubdirectory()
    {
        var root = GitHubRemoteDetector.FindGitRepositoryRoot(AppContext.BaseDirectory);

        Assert.NotNull(root);
        Assert.True(Directory.Exists(Path.Combine(root, ".git")),
            $"Expected .git directory at {root}");
    }

    [Fact]
    public void FindGitRepositoryRoot_ReturnsNull_WhenNoGitDirectory()
    {
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
        var searchPath = string.IsNullOrEmpty(systemRoot) ? "/" : systemRoot;

        var root = GitHubRemoteDetector.FindGitRepositoryRoot(searchPath);

        Assert.Null(root);
    }

    // ---- GetCandidateDirectories ----

    [Fact]
    public void GetCandidateDirectories_IncludesCurrentDirectory()
    {
        var candidates = GitHubRemoteDetector.GetCandidateDirectories();

        Assert.Contains(candidates, c =>
            string.Equals(c, Environment.CurrentDirectory, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetCandidateDirectories_IncludesGitRepoRoot_FromExecutableLocation()
    {
        var candidates = GitHubRemoteDetector.GetCandidateDirectories();

        var repoRoot = GitHubRemoteDetector.FindGitRepositoryRoot(AppContext.BaseDirectory);
        if (repoRoot is not null)
        {
            Assert.Contains(candidates, c =>
                string.Equals(c, repoRoot, StringComparison.OrdinalIgnoreCase));
        }
    }
}
