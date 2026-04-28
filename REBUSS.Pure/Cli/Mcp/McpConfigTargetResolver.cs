namespace REBUSS.Pure.Cli.Mcp;

/// <summary>
/// Picks which MCP configuration files <see cref="InitCommand"/> should write for a
/// given combination of <c>--agent</c>, explicit <c>--ide</c>, and IDE auto-detection.
/// Two entry points: <see cref="Resolve"/> for repository-local targets and
/// <see cref="ResolveGlobal"/> for user-level (global) targets. Owns the IDE-detection
/// helpers and the directory/filename constants used by the deployers.
/// </summary>
internal static class McpConfigTargetResolver
{
    internal const string VsCodeDir = ".vscode";
    internal const string VisualStudioDir = ".vs";
    internal const string ClaudeCodeDir = ".claude";
    internal const string ClaudeCodeMarkerFile = "CLAUDE.md";
    internal const string McpConfigFileName = "mcp.json";
    internal const string VsGlobalMcpConfigFileName = ".mcp.json";
    internal const string CopilotCliMcpConfigFileName = "mcp-config.json";
    internal const string ClaudeCodeMcpConfigFileName = ".mcp.json";
    internal const string ClaudeCodeGlobalConfigFileName = ".claude.json";

    /// <summary>
    /// Detects which IDE(s) are in use and returns the list of repository-local config
    /// file targets to write. When <paramref name="agent"/> is <c>"claude"</c>, returns
    /// the single Claude Code target (<c>.mcp.json</c> at repo root with the
    /// <c>mcpServers</c> top-level key). When <paramref name="ide"/> is provided
    /// (<c>"vscode"</c> or <c>"vs"</c>), only that IDE's target is returned — no
    /// auto-detection is performed. Otherwise selection is based on which IDE folders
    /// physically exist: only <c>.vscode</c> → VS Code; only <c>.vs</c> → Visual Studio;
    /// both or neither → both targets.
    /// </summary>
    internal static List<McpConfigTarget> Resolve(string gitRoot, string? ide = null, string? agent = null)
    {
        if (string.Equals(agent, CliArgumentParser.AgentClaude, StringComparison.OrdinalIgnoreCase))
            return
            [
                new McpConfigTarget(
                    "Claude Code",
                    gitRoot,
                    Path.Combine(gitRoot, ClaudeCodeMcpConfigFileName),
                    UseMcpServersKey: true)
            ];

        if (!string.IsNullOrWhiteSpace(ide))
        {
            if (string.Equals(ide, "vscode", StringComparison.OrdinalIgnoreCase))
                return
                [
                    new McpConfigTarget(
                        "VS Code",
                        Path.Combine(gitRoot, VsCodeDir),
                        Path.Combine(gitRoot, VsCodeDir, McpConfigFileName))
                ];

            if (string.Equals(ide, "vs", StringComparison.OrdinalIgnoreCase))
                return
                [
                    new McpConfigTarget(
                        "Visual Studio",
                        Path.Combine(gitRoot, VisualStudioDir),
                        Path.Combine(gitRoot, VisualStudioDir, McpConfigFileName))
                ];

            throw new ArgumentException($"Unrecognized --ide value '{ide}'. Supported values: vscode, vs.");
        }

        var targets = new List<McpConfigTarget>();

        bool hasVsCode = DetectsVsCode(gitRoot);
        bool hasVisualStudio = DetectsVisualStudio(gitRoot);

        bool writeVsCode = hasVsCode || !hasVisualStudio;
        bool writeVisualStudio = hasVisualStudio || !hasVsCode;

        if (writeVsCode)
            targets.Add(new McpConfigTarget(
                "VS Code",
                Path.Combine(gitRoot, VsCodeDir),
                Path.Combine(gitRoot, VsCodeDir, McpConfigFileName)));

        if (writeVisualStudio)
            targets.Add(new McpConfigTarget(
                "Visual Studio",
                Path.Combine(gitRoot, VisualStudioDir),
                Path.Combine(gitRoot, VisualStudioDir, McpConfigFileName)));

        return targets;
    }

    /// <summary>
    /// Returns global (user-level) MCP configuration targets. Branch on <paramref name="agent"/>:
    /// <list type="bullet">
    ///   <item><c>"claude"</c> → single <c>~/.claude.json</c> target with <c>mcpServers</c> key.</item>
    ///   <item><c>"copilot"</c> / <c>null</c> → VS <c>~/.mcp.json</c> + VS Code <c>%APPDATA%/Code/User/mcp.json</c> + Copilot CLI <c>~/.copilot/mcp-config.json</c>.</item>
    /// </list>
    /// </summary>
    internal static List<McpConfigTarget> ResolveGlobal(string? agent = null)
    {
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        if (string.Equals(agent, CliArgumentParser.AgentClaude, StringComparison.OrdinalIgnoreCase))
            return
            [
                new McpConfigTarget(
                    "Claude Code (global)",
                    userHome,
                    Path.Combine(userHome, ClaudeCodeGlobalConfigFileName),
                    UseMcpServersKey: true)
            ];

        return
        [
            new McpConfigTarget(
                "Visual Studio (global)",
                userHome,
                Path.Combine(userHome, VsGlobalMcpConfigFileName)),

            new McpConfigTarget(
                "VS Code (global)",
                Path.Combine(appData, "Code", "User"),
                Path.Combine(appData, "Code", "User", McpConfigFileName)),

            new McpConfigTarget(
                "Copilot CLI (global)",
                Path.Combine(userHome, ".copilot"),
                Path.Combine(userHome, ".copilot", CopilotCliMcpConfigFileName))
        ];
    }

    /// <summary>
    /// Returns true if the repository shows signs of being used with Claude Code
    /// (<c>.claude/</c> directory or <c>CLAUDE.md</c> marker file in the root).
    /// Used only as a heuristic hint — the agent choice itself is driven by
    /// the <c>--agent</c> flag or the interactive prompt.
    /// </summary>
    internal static bool DetectsClaudeCode(string gitRoot) =>
        Directory.Exists(Path.Combine(gitRoot, ClaudeCodeDir)) ||
        File.Exists(Path.Combine(gitRoot, ClaudeCodeMarkerFile));

    internal static bool DetectsVsCode(string gitRoot) =>
        Directory.Exists(Path.Combine(gitRoot, VsCodeDir)) ||
        Directory.EnumerateFiles(gitRoot, "*.code-workspace", SearchOption.TopDirectoryOnly).Any();

    internal static bool DetectsVisualStudio(string gitRoot) =>
        Directory.Exists(Path.Combine(gitRoot, VisualStudioDir)) ||
        Directory.EnumerateFiles(gitRoot, "*.sln", SearchOption.TopDirectoryOnly).Any();
}
