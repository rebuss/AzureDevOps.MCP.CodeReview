using REBUSS.Pure.Cli.Deployment;

namespace REBUSS.Pure.Tests.Cli.Deployment;

/// <summary>
/// Step 5 — focused unit tests for <see cref="LegacyClaudeCommandBackup"/>. Confirms
/// the safety convention: pre-024 <c>.claude/commands/&lt;skill&gt;.md</c> files are moved to
/// <c>.bak</c>; unrelated user files in the same directory are left alone; a missing
/// directory is a no-op.
/// </summary>
public class LegacyClaudeCommandBackupTests
{
    [Fact]
    public async Task RunAsync_MovesLegacyCommandToBak()
    {
        using var temp = new TempDir();
        var commandsDir = Path.Combine(temp.Path, ".claude", "commands");
        Directory.CreateDirectory(commandsDir);
        var legacyPath = Path.Combine(commandsDir, "review-pr.md");
        await File.WriteAllTextAsync(legacyPath, "legacy command body");

        var output = new StringWriter();
        await new LegacyClaudeCommandBackup(output).RunAsync(temp.Path, CancellationToken.None);

        Assert.False(File.Exists(legacyPath));
        Assert.True(File.Exists(legacyPath + ".bak"));
        var bakBody = await File.ReadAllTextAsync(legacyPath + ".bak");
        Assert.Equal("legacy command body", bakBody);
    }

    [Fact]
    public async Task RunAsync_LeavesUnrelatedUserFilesAlone()
    {
        using var temp = new TempDir();
        var commandsDir = Path.Combine(temp.Path, ".claude", "commands");
        Directory.CreateDirectory(commandsDir);
        var userPath = Path.Combine(commandsDir, "my-custom.md");
        await File.WriteAllTextAsync(userPath, "user-defined command");

        await new LegacyClaudeCommandBackup(new StringWriter()).RunAsync(temp.Path, CancellationToken.None);

        Assert.True(File.Exists(userPath));
        Assert.False(File.Exists(userPath + ".bak"));
    }

    [Fact]
    public async Task RunAsync_MissingCommandsDirectory_IsNoOp()
    {
        using var temp = new TempDir();

        var output = new StringWriter();
        await new LegacyClaudeCommandBackup(output).RunAsync(temp.Path, CancellationToken.None);

        Assert.Equal(string.Empty, output.ToString());
        Assert.False(Directory.Exists(Path.Combine(temp.Path, ".claude", "commands")));
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
