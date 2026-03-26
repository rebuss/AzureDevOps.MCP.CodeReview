using REBUSS.Pure.SmokeTests.Fixtures;

namespace REBUSS.Pure.SmokeTests.InitCommand;

/// <summary>
/// Smoke tests for <c>rebuss-pure init</c> in an Azure DevOps-remote repository.
/// </summary>
[Trait("Category", "Smoke")]
public class AzureDevOpsInitSmokeTests
{
    [Fact]
    public async Task Init_WithPat_CreatesConfigAndPrompts_InAzureDevOpsRepo()
    {
        using var repo = TempGitRepoFixture.Create("https://dev.azure.com/fake-org/fake-project/_git/fake-repo");

        var result = await CliProcessHelper.RunAsync(
            repo.RootPath,
            "init --pat fake-ado-token",
            stdin: "n\n");

        Assert.Equal(0, result.ExitCode);

        Assert.True(repo.FileExists(Path.Combine(".vscode", "mcp.json")));
        Assert.True(repo.FileExists(Path.Combine(".vs", "mcp.json")));

        var config = repo.ReadFile(Path.Combine(".vscode", "mcp.json"));
        Assert.Contains("REBUSS.Pure", config);
        Assert.Contains("--repo", config);
        Assert.Contains("--pat", config);
        Assert.Contains("fake-ado-token", config);

        Assert.True(repo.FileExists(Path.Combine(".github", "prompts", "review-pr.md")));
        Assert.True(repo.FileExists(Path.Combine(".github", "prompts", "self-review.md")));
        Assert.False(repo.FileExists(Path.Combine(".github", "prompts", "create-pr.md")));

        // Instruction files should be copied
        Assert.True(repo.FileExists(Path.Combine(".github", "instructions", "review-pr.instructions.md")));
        Assert.True(repo.FileExists(Path.Combine(".github", "instructions", "self-review.instructions.md")));
    }

    [Fact]
    public async Task Init_WithoutPat_StillCreatesFiles_WhenAzCliDeclined()
    {
        using var repo = TempGitRepoFixture.Create("https://dev.azure.com/fake-org/fake-project/_git/fake-repo");

        // Restricted PATH hides az CLI → triggers "CLI not installed" prompt.
        // "n" declines the install prompt.
        var result = await CliProcessHelper.RunAsync(
            repo.RootPath,
            "init",
            stdin: "n\n",
            environmentOverrides: CliProcessHelper.BuildRestrictedPathEnv());

        Assert.Equal(0, result.ExitCode);
        Assert.True(repo.FileExists(Path.Combine(".vscode", "mcp.json")));
        Assert.True(repo.FileExists(Path.Combine(".github", "prompts", "review-pr.md")));
        Assert.True(repo.FileExists(Path.Combine(".github", "instructions", "review-pr.instructions.md")));

        var config = repo.ReadFile(Path.Combine(".vscode", "mcp.json"));
        Assert.DoesNotContain("--pat", config);
    }

    [Fact]
    public async Task Init_WithSshRemote_DetectsAzureDevOps()
    {
        using var repo = TempGitRepoFixture.Create("git@ssh.dev.azure.com:v3/fake-org/fake-project/fake-repo");

        var result = await CliProcessHelper.RunAsync(
            repo.RootPath,
            "init --pat fake-token",
            stdin: "n\n");

        Assert.Equal(0, result.ExitCode);
        Assert.True(repo.FileExists(Path.Combine(".vscode", "mcp.json")));
    }

    [Fact]
    public async Task Init_OutsideGitRepo_ReturnsError()
    {
        using var dir = TempGitRepoFixture.CreateNonGitDirectory();

        var result = await CliProcessHelper.RunAsync(
            dir.RootPath,
            "init --pat fake-token");

        Assert.Equal(1, result.ExitCode);

        var output = result.StdOut + result.StdErr;
        Assert.Contains("Not inside a Git repository", output);
    }

    [Fact]
    public async Task Init_FromSubdirectory_FindsGitRoot()
    {
        using var repo = TempGitRepoFixture.Create("https://dev.azure.com/fake-org/proj/_git/repo");
        repo.CreateDirectory(Path.Combine("src", "app"));

        var subDir = Path.Combine(repo.RootPath, "src", "app");

        var result = await CliProcessHelper.RunAsync(
            subDir,
            "init --pat fake-token",
            stdin: "n\n");

        Assert.Equal(0, result.ExitCode);

        // Config should be at the git root, not in the subdirectory
        Assert.True(repo.FileExists(Path.Combine(".vscode", "mcp.json")));
        Assert.False(File.Exists(Path.Combine(subDir, ".vscode", "mcp.json")));
    }
}
