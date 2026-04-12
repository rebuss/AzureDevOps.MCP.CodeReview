using REBUSS.Pure.SmokeTests.Fixtures;

namespace REBUSS.Pure.SmokeTests.InitCommand;

/// <summary>
/// Smoke tests for <c>rebuss-pure init</c> in a GitHub-remote repository.
/// These tests run the real compiled binary as a child process.
/// </summary>
[Trait("Category", "Smoke")]
public class GitHubInitSmokeTests
{
    [Fact]
    public async Task Init_WithPat_CreatesConfigAndPrompts_InGitHubRepo()
    {
        using var repo = TempGitRepoFixture.Create("https://github.com/fake-org/fake-repo.git");

        var result = await CliProcessHelper.RunAsync(
            repo.RootPath,
            "init --pat fake-github-token",
            stdin: "n\n");

        Assert.Equal(0, result.ExitCode);

        // mcp.json should be created (both IDEs when no markers exist)
        Assert.True(repo.FileExists(Path.Combine(".vscode", "mcp.json")),
            "Expected .vscode/mcp.json to be created.");
        Assert.True(repo.FileExists(Path.Combine(".vs", "mcp.json")),
            "Expected .vs/mcp.json to be created.");

        // Config should contain expected entries
        var config = repo.ReadFile(Path.Combine(".vscode", "mcp.json"));
        Assert.Contains("REBUSS.Pure", config);
        Assert.Contains("--repo", config);
        Assert.Contains("--pat", config);
        Assert.Contains("fake-github-token", config);

        // Prompt files should be copied
        Assert.True(repo.FileExists(Path.Combine(".github", "prompts", "review-pr.prompt.md")),
            "Expected review-pr.prompt.md prompt to be copied.");
        Assert.True(repo.FileExists(Path.Combine(".github", "prompts", "self-review.prompt.md")),
            "Expected self-review.prompt.md prompt to be copied.");
        Assert.False(repo.FileExists(Path.Combine(".github", "prompts", "create-pr.md")),
            "Expected create-pr.md prompt to be not copied.");

        // init must not create instruction files
        Assert.False(repo.FileExists(Path.Combine(".github", "instructions", "review-pr.instructions.md")),
            "init must not create review-pr.instructions.md.");
        Assert.False(repo.FileExists(Path.Combine(".github", "instructions", "self-review.instructions.md")),
            "init must not create self-review.instructions.md.");
    }

    [Fact]
    public async Task Init_WithoutPat_StillCreatesFiles_WhenGhCliDeclined()
    {
        using var repo = TempGitRepoFixture.Create("https://github.com/fake-org/fake-repo.git");

        // Restricted PATH hides gh CLI → triggers "CLI not installed" prompt.
        // "n" declines the install prompt.
        using var restrictedPath = CliProcessHelper.BuildRestrictedPathEnv();
        var result = await CliProcessHelper.RunAsync(
            repo.RootPath,
            "init",
            stdin: "n\n",
            environmentOverrides: restrictedPath.Env);

        // Init should succeed (exit 0) even when auth fails
        Assert.Equal(0, result.ExitCode);

        // Config and prompts should still be created (they're written before auth)
        Assert.True(repo.FileExists(Path.Combine(".vscode", "mcp.json")));
        Assert.True(repo.FileExists(Path.Combine(".github", "prompts", "review-pr.prompt.md")));
        Assert.False(repo.FileExists(Path.Combine(".github", "instructions", "review-pr.instructions.md")));

        // Config should NOT contain --pat when none was provided
        var config = repo.ReadFile(Path.Combine(".vscode", "mcp.json"));
        Assert.DoesNotContain("--pat", config);
    }

    [Fact]
    public async Task Init_DetectsVsCodeMarker_CreatesOnlyVsCodeConfig()
    {
        using var repo = TempGitRepoFixture.Create("https://github.com/fake-org/fake-repo.git");
        repo.CreateDirectory(".vscode");

        var result = await CliProcessHelper.RunAsync(
            repo.RootPath,
            "init --pat fake-token",
            stdin: "n\n");

        Assert.Equal(0, result.ExitCode);
        Assert.True(repo.FileExists(Path.Combine(".vscode", "mcp.json")));
        Assert.False(repo.FileExists(Path.Combine(".vs", "mcp.json")),
            "Should NOT create .vs/mcp.json when only VS Code marker is present.");
    }

    [Fact]
    public async Task Init_DetectsVisualStudioMarker_CreatesOnlyVsConfig()
    {
        using var repo = TempGitRepoFixture.Create("https://github.com/fake-org/fake-repo.git");
        repo.CreateFile("MySolution.sln", "");

        var result = await CliProcessHelper.RunAsync(
            repo.RootPath,
            "init --pat fake-token",
            stdin: "n\n");

        Assert.Equal(0, result.ExitCode);
        Assert.True(repo.FileExists(Path.Combine(".vs", "mcp.json")));
        Assert.False(repo.FileExists(Path.Combine(".vscode", "mcp.json")),
            "Should NOT create .vscode/mcp.json when only Visual Studio marker is present.");
    }

    [Fact]
    public async Task Init_MergesIntoExistingMcpJson_PreservesOtherServers()
    {
        using var repo = TempGitRepoFixture.Create("https://github.com/fake-org/fake-repo.git");
        repo.CreateDirectory(".vscode");

        var existingConfig = """
            {
              "servers": {
                "other-server": {
                  "type": "stdio",
                  "command": "other-tool"
                }
              }
            }
            """;
        repo.CreateFile(Path.Combine(".vscode", "mcp.json"), existingConfig);

        var result = await CliProcessHelper.RunAsync(
            repo.RootPath,
            "init --pat fake-token",
            stdin: "n\n");

        Assert.Equal(0, result.ExitCode);

        var config = repo.ReadFile(Path.Combine(".vscode", "mcp.json"));
        Assert.Contains("REBUSS.Pure", config);
        Assert.Contains("other-server", config);
    }
}
