namespace REBUSS.Pure.Cli;

/// <summary>
/// Describes a single MCP configuration file target to be written by <see cref="InitCommand"/>.
/// <paramref name="UseMcpServersKey"/> controls which top-level key is used in the JSON output:
/// <c>"servers"</c> (VS / VS Code) when <c>false</c>, <c>"mcpServers"</c> (Claude Code) when <c>true</c>.
/// Namespace intentionally kept as <c>REBUSS.Pure.Cli</c> (not <c>REBUSS.Pure.Cli.Mcp</c>) so
/// existing test files and the <c>Func&lt;List&lt;McpConfigTarget&gt;&gt;</c> global-targets resolver seam
/// continue to compile without renaming usings.
/// </summary>
internal sealed record McpConfigTarget(string IdeName, string Directory, string ConfigPath, bool UseMcpServersKey = false);
