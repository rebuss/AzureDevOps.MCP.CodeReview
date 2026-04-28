using REBUSS.Pure.Cli.Deployment;

namespace REBUSS.Pure.Tests.Cli.Deployment;

/// <summary>
/// Step 5 — focused unit tests for <see cref="ClaudeSkillDeployer"/>. Verifies the
/// drift policy: identical content is a no-op log; modified content is backed up
/// to <c>SKILL.md.bak</c> before re-deployment.
/// </summary>
public class ClaudeSkillDeployerTests
{
    [Fact]
    public async Task DeployAsync_FreshTarget_WritesSkillsAtExpectedPaths()
    {
        using var temp = new TempDir();
        var output = new StringWriter();

        await new ClaudeSkillDeployer(output).DeployAsync(temp.Path, CancellationToken.None);

        var reviewSkill = Path.Combine(temp.Path, ".claude", "skills", "review-pr", "SKILL.md");
        var selfSkill = Path.Combine(temp.Path, ".claude", "skills", "self-review", "SKILL.md");
        Assert.True(File.Exists(reviewSkill));
        Assert.True(File.Exists(selfSkill));
    }

    [Fact]
    public async Task DeployAsync_IdenticalContent_DoesNotCreateBackup_AndLogsUnchanged()
    {
        using var temp = new TempDir();
        await new ClaudeSkillDeployer(new StringWriter()).DeployAsync(temp.Path, CancellationToken.None);

        var output2 = new StringWriter();
        await new ClaudeSkillDeployer(output2).DeployAsync(temp.Path, CancellationToken.None);

        var reviewSkillBak = Path.Combine(temp.Path, ".claude", "skills", "review-pr", "SKILL.md.bak");
        Assert.False(File.Exists(reviewSkillBak));
        Assert.Contains("review-pr", output2.ToString());
    }

    [Fact]
    public async Task DeployAsync_DriftedContent_CreatesBakBeforeOverwriting()
    {
        using var temp = new TempDir();
        await new ClaudeSkillDeployer(new StringWriter()).DeployAsync(temp.Path, CancellationToken.None);

        var skillPath = Path.Combine(temp.Path, ".claude", "skills", "review-pr", "SKILL.md");
        await File.WriteAllTextAsync(skillPath, "user-edited content — should trigger .bak");

        var output = new StringWriter();
        await new ClaudeSkillDeployer(output).DeployAsync(temp.Path, CancellationToken.None);

        var bakPath = skillPath + ".bak";
        Assert.True(File.Exists(bakPath));
        var bakContent = await File.ReadAllTextAsync(bakPath);
        Assert.Equal("user-edited content — should trigger .bak", bakContent);

        var current = await File.ReadAllTextAsync(skillPath);
        Assert.DoesNotContain("user-edited content", current);
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
