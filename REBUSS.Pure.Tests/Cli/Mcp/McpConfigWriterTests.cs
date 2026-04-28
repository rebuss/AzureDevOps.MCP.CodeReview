using REBUSS.Pure.Cli;
using REBUSS.Pure.Cli.Mcp;

namespace REBUSS.Pure.Tests.Cli.Mcp;

/// <summary>
/// Step 6 — focused unit tests for <see cref="McpConfigWriter.WriteAllAsync"/>.
/// File-locked-by-running-client behaviour is exercised through the integration
/// tests in <c>InitCommandTests</c>; the unit tests here cover the create / update
/// happy paths plus the path-normalization seam.
/// </summary>
public class McpConfigWriterTests
{
    [Fact]
    public async Task WriteAllAsync_NewPath_CreatesDirectory_WritesFile_NoBackup_EmitsCreatedLog()
    {
        using var temp = new TempDir();
        var configDir = Path.Combine(temp.Path, ".vscode");
        var configPath = Path.Combine(configDir, "mcp.json");
        var target = new McpConfigTarget("VS Code", configDir, configPath);

        var output = new StringWriter();
        await new McpConfigWriter(output).WriteAllAsync(
            new[] { target },
            executablePath: @"C:\tools\REBUSS.Pure.exe",
            gitRoot: temp.Path,
            pat: null,
            agent: "copilot",
            CancellationToken.None);

        Assert.True(File.Exists(configPath));
        Assert.False(File.Exists(configPath + ".bak"));
        var written = await File.ReadAllTextAsync(configPath);
        Assert.Contains("\"REBUSS.Pure\"", written);
        Assert.Contains("Created", output.ToString());
    }

    [Fact]
    public async Task WriteAllAsync_ExistingValidJson_BacksUpAndEmitsUpdatedLog_AndMergesContent()
    {
        using var temp = new TempDir();
        var configDir = Path.Combine(temp.Path, ".vscode");
        Directory.CreateDirectory(configDir);
        var configPath = Path.Combine(configDir, "mcp.json");
        await File.WriteAllTextAsync(configPath,
            """
            {
              "servers": {
                "OtherTool": { "type": "stdio", "command": "other.exe", "args": [] }
              }
            }
            """);
        var target = new McpConfigTarget("VS Code", configDir, configPath);

        var output = new StringWriter();
        await new McpConfigWriter(output).WriteAllAsync(
            new[] { target },
            executablePath: @"C:\tools\REBUSS.Pure.exe",
            gitRoot: temp.Path,
            pat: null,
            agent: null,
            CancellationToken.None);

        Assert.True(File.Exists(configPath + ".bak"));
        var current = await File.ReadAllTextAsync(configPath);
        Assert.Contains("\"OtherTool\"", current);
        Assert.Contains("\"REBUSS.Pure\"", current);
        Assert.Contains("Updated", output.ToString());
    }

    [Fact]
    public async Task WriteAllAsync_MultipleTargets_EachWrittenIndependently()
    {
        using var temp = new TempDir();
        var vsCodeDir = Path.Combine(temp.Path, ".vscode");
        var vsDir = Path.Combine(temp.Path, ".vs");
        var vsCodePath = Path.Combine(vsCodeDir, "mcp.json");
        var vsPath = Path.Combine(vsDir, "mcp.json");

        var targets = new[]
        {
            new McpConfigTarget("VS Code", vsCodeDir, vsCodePath),
            new McpConfigTarget("Visual Studio", vsDir, vsPath),
        };

        var output = new StringWriter();
        await new McpConfigWriter(output).WriteAllAsync(
            targets,
            executablePath: @"C:\tools\REBUSS.Pure.exe",
            gitRoot: temp.Path,
            pat: "secret",
            agent: "copilot",
            CancellationToken.None);

        Assert.True(File.Exists(vsCodePath));
        Assert.True(File.Exists(vsPath));
        var vsCodeContent = await File.ReadAllTextAsync(vsCodePath);
        var vsContent = await File.ReadAllTextAsync(vsPath);
        Assert.Contains("\"--pat\"", vsCodeContent);
        Assert.Contains("\"--pat\"", vsContent);
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
