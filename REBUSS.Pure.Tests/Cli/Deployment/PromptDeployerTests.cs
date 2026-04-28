using REBUSS.Pure.Cli.Deployment;

namespace REBUSS.Pure.Tests.Cli.Deployment;

/// <summary>
/// Step 5 — focused unit tests for <see cref="PromptDeployer"/>. Uses temp directories
/// to verify file-system side effects without touching the real repo.
/// </summary>
public class PromptDeployerTests
{
    [Fact]
    public async Task DeployAsync_WritesPromptFiles_AndEmitsCountLog()
    {
        using var temp = new TempDir();
        var output = new StringWriter();

        await new PromptDeployer(output).DeployAsync(temp.Path, CancellationToken.None);

        var promptsDir = Path.Combine(temp.Path, ".github", "prompts");
        Assert.True(File.Exists(Path.Combine(promptsDir, "review-pr.prompt.md")));
        Assert.True(File.Exists(Path.Combine(promptsDir, "self-review.prompt.md")));

        // Count log includes the integer "2" — both prompts written.
        Assert.Contains("2", output.ToString());
    }

    [Fact]
    public async Task DeployAsync_DeletesLegacyNonSuffixedPrompts_WhenPresent()
    {
        using var temp = new TempDir();
        var promptsDir = Path.Combine(temp.Path, ".github", "prompts");
        Directory.CreateDirectory(promptsDir);

        // Legacy file from prior versions (without the .prompt suffix).
        var legacyPath = Path.Combine(promptsDir, "review-pr.md");
        await File.WriteAllTextAsync(legacyPath, "legacy content");

        var output = new StringWriter();
        await new PromptDeployer(output).DeployAsync(temp.Path, CancellationToken.None);

        Assert.False(File.Exists(legacyPath));
        Assert.Contains(legacyPath, output.ToString());
    }

    [Fact]
    public async Task DeployAsync_OverwritesExistingPromptFile_OnRepeatedRun()
    {
        using var temp = new TempDir();
        var output1 = new StringWriter();
        await new PromptDeployer(output1).DeployAsync(temp.Path, CancellationToken.None);

        var promptPath = Path.Combine(temp.Path, ".github", "prompts", "review-pr.prompt.md");
        await File.WriteAllTextAsync(promptPath, "user-modified — should be overwritten");

        var output2 = new StringWriter();
        await new PromptDeployer(output2).DeployAsync(temp.Path, CancellationToken.None);

        var content = await File.ReadAllTextAsync(promptPath);
        Assert.DoesNotContain("user-modified", content);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
