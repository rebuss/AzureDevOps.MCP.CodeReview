using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using REBUSS.Pure.Mcp;

namespace REBUSS.Pure.Tests.Mcp;

public class McpWorkspaceRootProviderTests
{
    private McpWorkspaceRootProvider CreateProvider(string? localRepoPath = null)
    {
        var configValues = new Dictionary<string, string?>();
        if (localRepoPath is not null)
            configValues["AzureDevOps:LocalRepoPath"] = localRepoPath;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        return new McpWorkspaceRootProvider(
            configuration,
            NullLogger<McpWorkspaceRootProvider>.Instance);
    }

    // --- SetRoots / GetRootUris ---

    [Fact]
    public void GetRootUris_ReturnsEmpty_WhenNoRootsSet()
    {
        var provider = CreateProvider();

        var roots = provider.GetRootUris();

        Assert.Empty(roots);
    }

    [Fact]
    public void GetRootUris_ReturnsStoredRoots_AfterSetRoots()
    {
        var provider = CreateProvider();

        provider.SetRoots(new[] { "file:///c:/repo1", "file:///c:/repo2" });

        var roots = provider.GetRootUris();
        Assert.Equal(2, roots.Count);
        Assert.Equal("file:///c:/repo1", roots[0]);
        Assert.Equal("file:///c:/repo2", roots[1]);
    }

    [Fact]
    public void SetRoots_ReplacesExistingRoots()
    {
        var provider = CreateProvider();

        provider.SetRoots(new[] { "file:///c:/first" });
        provider.SetRoots(new[] { "file:///c:/second" });

        var roots = provider.GetRootUris();
        Assert.Single(roots);
        Assert.Equal("file:///c:/second", roots[0]);
    }

    // --- ConvertUriToLocalPath ---

    [Fact]
    public void ConvertUriToLocalPath_ConvertsFileUri()
    {
        var result = McpWorkspaceRootProvider.ConvertUriToLocalPath("file:///c:/projects/repo");

        Assert.NotNull(result);
        Assert.Equal("c:/projects/repo", result.Replace('\\', '/'));
    }

    [Fact]
    public void ConvertUriToLocalPath_ReturnsNull_ForHttpUri()
    {
        var result = McpWorkspaceRootProvider.ConvertUriToLocalPath("https://example.com/repo");

        Assert.Null(result);
    }

    [Fact]
    public void ConvertUriToLocalPath_ReturnsNull_ForInvalidUri()
    {
        var result = McpWorkspaceRootProvider.ConvertUriToLocalPath("not-a-uri");

        Assert.Null(result);
    }

    [Fact]
    public void ConvertUriToLocalPath_ReturnsNull_ForEmptyString()
    {
        var result = McpWorkspaceRootProvider.ConvertUriToLocalPath(string.Empty);

        Assert.Null(result);
    }

    // --- FindGitRepositoryRoot ---

    [Fact]
    public void FindGitRepositoryRoot_FindsRoot_WhenStartingFromSubdirectory()
    {
        var root = McpWorkspaceRootProvider.FindGitRepositoryRoot(AppContext.BaseDirectory);

        Assert.NotNull(root);
        Assert.True(Directory.Exists(Path.Combine(root, ".git")));
    }

    [Fact]
    public void FindGitRepositoryRoot_ReturnsNull_WhenNoGitDirectory()
    {
        var root = McpWorkspaceRootProvider.FindGitRepositoryRoot(
            Path.GetPathRoot(Environment.SystemDirectory)!);

        Assert.Null(root);
    }

    // --- ResolveRepositoryRoot ---

    [Fact]
    public void ResolveRepositoryRoot_ReturnsNull_WhenNoRootsAndNoLocalRepoPath()
    {
        var provider = CreateProvider();

        var result = provider.ResolveRepositoryRoot();

        Assert.Null(result);
    }

    [Fact]
    public void ResolveRepositoryRoot_ReturnsRoot_FromMcpRootUri()
    {
        var provider = CreateProvider();
        var repoRoot = McpWorkspaceRootProvider.FindGitRepositoryRoot(AppContext.BaseDirectory);
        Assert.NotNull(repoRoot);

        provider.SetRoots(new[] { new Uri(repoRoot).AbsoluteUri });

        var result = provider.ResolveRepositoryRoot();

        Assert.Equal(repoRoot, result);
    }

    [Fact]
    public void ResolveRepositoryRoot_ReturnsRoot_FromLocalRepoPath()
    {
        var repoRoot = McpWorkspaceRootProvider.FindGitRepositoryRoot(AppContext.BaseDirectory);
        Assert.NotNull(repoRoot);

        var provider = CreateProvider(localRepoPath: repoRoot);

        var result = provider.ResolveRepositoryRoot();

        Assert.Equal(repoRoot, result);
    }

    [Fact]
    public void ResolveRepositoryRoot_PrefersRoot_OverLocalRepoPath()
    {
        var repoRoot = McpWorkspaceRootProvider.FindGitRepositoryRoot(AppContext.BaseDirectory);
        Assert.NotNull(repoRoot);

        var provider = CreateProvider(localRepoPath: repoRoot);
        provider.SetRoots(new[] { new Uri(repoRoot).AbsoluteUri });

        var result = provider.ResolveRepositoryRoot();

        Assert.Equal(repoRoot, result);
    }

    [Fact]
    public void ResolveRepositoryRoot_ReturnsNull_WhenLocalRepoPathDoesNotExist()
    {
        var provider = CreateProvider(localRepoPath: @"C:\NonExistent\Path\That\Does\Not\Exist");

        var result = provider.ResolveRepositoryRoot();

        Assert.Null(result);
    }

    [Fact]
    public void ResolveRepositoryRoot_SkipsNonFileUris()
    {
        var provider = CreateProvider();
        provider.SetRoots(new[] { "https://example.com/repo" });

        var result = provider.ResolveRepositoryRoot();

        Assert.Null(result);
    }

    [Fact]
    public void ResolveRepositoryRoot_SkipsNonExistentMcpRootDirectories()
    {
        var provider = CreateProvider();
        provider.SetRoots(new[] { "file:///C:/NonExistent/Path" });

        var result = provider.ResolveRepositoryRoot();

        Assert.Null(result);
    }

    // --- CLI --repo argument ---

    [Fact]
    public void ResolveRepositoryRoot_ReturnsRoot_FromCliRepoPath()
    {
        var repoRoot = McpWorkspaceRootProvider.FindGitRepositoryRoot(AppContext.BaseDirectory);
        Assert.NotNull(repoRoot);

        var provider = CreateProvider();
        provider.SetCliRepositoryPath(repoRoot);

        var result = provider.ResolveRepositoryRoot();

        Assert.Equal(repoRoot, result);
    }

    [Fact]
    public void ResolveRepositoryRoot_CliRepoPath_TakesPrecedenceOverMcpRoots()
    {
        var repoRoot = McpWorkspaceRootProvider.FindGitRepositoryRoot(AppContext.BaseDirectory);
        Assert.NotNull(repoRoot);

        var provider = CreateProvider();
        provider.SetCliRepositoryPath(repoRoot);
        provider.SetRoots(new[] { "file:///C:/NonExistent/Path" });

        var result = provider.ResolveRepositoryRoot();

        Assert.Equal(repoRoot, result);
    }

    [Fact]
    public void ResolveRepositoryRoot_CliRepoPath_TakesPrecedenceOverLocalRepoPath()
    {
        var repoRoot = McpWorkspaceRootProvider.FindGitRepositoryRoot(AppContext.BaseDirectory);
        Assert.NotNull(repoRoot);

        var provider = CreateProvider(localRepoPath: @"C:\NonExistent\Path");
        provider.SetCliRepositoryPath(repoRoot);

        var result = provider.ResolveRepositoryRoot();

        Assert.Equal(repoRoot, result);
    }

    [Fact]
    public void ResolveRepositoryRoot_FallsBackToMcpRoots_WhenCliRepoPathDoesNotExist()
    {
        var repoRoot = McpWorkspaceRootProvider.FindGitRepositoryRoot(AppContext.BaseDirectory);
        Assert.NotNull(repoRoot);

        var provider = CreateProvider();
        provider.SetCliRepositoryPath(@"C:\NonExistent\CLI\Path");
        provider.SetRoots(new[] { new Uri(repoRoot).AbsoluteUri });

        var result = provider.ResolveRepositoryRoot();

        Assert.Equal(repoRoot, result);
    }

    // --- IsUnexpandedVariable ---

    [Theory]
    [InlineData("${workspaceFolder}")]
    [InlineData("${SomeVar}")]
    [InlineData("$(SolutionDir)")]
    [InlineData("C:\\repos\\${workspaceFolder}")]
    public void SetCliRepositoryPath_IgnoresUnexpandedVariables(string path)
    {
        var provider = CreateProvider();

        provider.SetCliRepositoryPath(path);

        Assert.Null(provider.ResolveRepositoryRoot());
    }

    [Theory]
    [InlineData("${workspaceFolder}", true)]
    [InlineData("$(SolutionDir)", true)]
    [InlineData(@"C:\Projects\MyRepo", false)]
    [InlineData("/home/user/repo", false)]
    [InlineData("", false)]
    public void IsUnexpandedVariable_DetectsPlaceholders(string path, bool expected)
    {
        Assert.Equal(expected, McpWorkspaceRootProvider.IsUnexpandedVariable(path));
    }
}
