using REBUSS.Pure.Cli;
using REBUSS.Pure.Cli.Mcp;

namespace REBUSS.Pure.Tests.Cli.Mcp;

/// <summary>
/// Step 4 — focused unit tests for the extracted <see cref="McpConfigTargetResolver"/>.
/// Uses a small <see cref="TempRepoDirectory"/> helper for IDE auto-detection cases
/// (mirrors the disposable temp-dir pattern used throughout <c>InitCommandTests</c>).
/// </summary>
public class McpConfigTargetResolverTests
{
    [Fact]
    public void Resolve_AgentClaude_LocalMode_ReturnsSingleClaudeTarget_WithMcpServersKey()
    {
        using var temp = new TempRepoDirectory();

        var targets = McpConfigTargetResolver.Resolve(temp.Path, ide: null, agent: "claude");

        var single = Assert.Single(targets);
        Assert.Equal("Claude Code", single.IdeName);
        Assert.True(single.UseMcpServersKey);
        Assert.EndsWith(".mcp.json", single.ConfigPath);
    }

    [Fact]
    public void ResolveGlobal_AgentClaude_ReturnsSingleClaudeJsonTarget()
    {
        var targets = McpConfigTargetResolver.ResolveGlobal(agent: "claude");

        var single = Assert.Single(targets);
        Assert.Equal("Claude Code (global)", single.IdeName);
        Assert.True(single.UseMcpServersKey);
        Assert.EndsWith(".claude.json", single.ConfigPath);
    }

    [Fact]
    public void Resolve_IdeVscode_ReturnsSingleVsCodeTarget()
    {
        using var temp = new TempRepoDirectory();

        var targets = McpConfigTargetResolver.Resolve(temp.Path, ide: "vscode");

        var single = Assert.Single(targets);
        Assert.Equal("VS Code", single.IdeName);
        Assert.False(single.UseMcpServersKey);
        Assert.EndsWith(Path.Combine(".vscode", "mcp.json"), single.ConfigPath);
    }

    [Fact]
    public void Resolve_IdeVs_ReturnsSingleVisualStudioTarget()
    {
        using var temp = new TempRepoDirectory();

        var targets = McpConfigTargetResolver.Resolve(temp.Path, ide: "vs");

        var single = Assert.Single(targets);
        Assert.Equal("Visual Studio", single.IdeName);
        Assert.False(single.UseMcpServersKey);
        Assert.EndsWith(Path.Combine(".vs", "mcp.json"), single.ConfigPath);
    }

    [Fact]
    public void Resolve_BogusIdeValue_ThrowsArgumentException()
    {
        using var temp = new TempRepoDirectory();

        Assert.Throws<ArgumentException>(() =>
            McpConfigTargetResolver.Resolve(temp.Path, ide: "rider"));
    }

    [Fact]
    public void Resolve_AutoDetect_OnlyVscodeFolder_ReturnsVsCodeOnly()
    {
        using var temp = new TempRepoDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, ".vscode"));

        var targets = McpConfigTargetResolver.Resolve(temp.Path);

        var single = Assert.Single(targets);
        Assert.Equal("VS Code", single.IdeName);
    }

    [Fact]
    public void Resolve_AutoDetect_OnlySolutionAtRoot_ReturnsVisualStudioOnly()
    {
        using var temp = new TempRepoDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "Sample.sln"), "Microsoft Visual Studio Solution File\n");

        var targets = McpConfigTargetResolver.Resolve(temp.Path);

        var single = Assert.Single(targets);
        Assert.Equal("Visual Studio", single.IdeName);
    }

    [Fact]
    public void Resolve_AutoDetect_NoMarkers_ReturnsBothTargets()
    {
        using var temp = new TempRepoDirectory();

        var targets = McpConfigTargetResolver.Resolve(temp.Path);

        Assert.Equal(2, targets.Count);
        Assert.Contains(targets, t => t.IdeName == "VS Code");
        Assert.Contains(targets, t => t.IdeName == "Visual Studio");
    }

    [Fact]
    public void ResolveGlobal_NullAgent_ReturnsThreeCopilotTargets()
    {
        var targets = McpConfigTargetResolver.ResolveGlobal(agent: null);

        Assert.Equal(3, targets.Count);
        Assert.Contains(targets, t => t.IdeName == "Visual Studio (global)");
        Assert.Contains(targets, t => t.IdeName == "VS Code (global)");
        Assert.Contains(targets, t => t.IdeName == "Copilot CLI (global)");
        Assert.All(targets, t => Assert.False(t.UseMcpServersKey));
    }

    private sealed class TempRepoDirectory : IDisposable
    {
        public string Path { get; }

        public TempRepoDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup — keeping a stale temp dir is acceptable.
            }
        }
    }
}
