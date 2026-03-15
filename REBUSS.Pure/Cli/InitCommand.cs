namespace REBUSS.Pure.Cli;

/// <summary>
/// Generates a <c>.vscode/mcp.json</c> configuration file in the current directory
/// so that MCP clients (e.g. VS Code, GitHub Copilot) can launch the server
/// with the correct <c>--repo</c> argument pointing to the workspace.
/// </summary>
public class InitCommand : ICliCommand
{
    private const string VsCodeDir = ".vscode";
    private const string McpConfigFileName = "mcp.json";

    private readonly TextWriter _output;
    private readonly string _workingDirectory;
    private readonly string _executablePath;

    public string Name => "init";

    public InitCommand(TextWriter output, string workingDirectory, string executablePath)
    {
        _output = output;
        _workingDirectory = workingDirectory;
        _executablePath = executablePath;
    }

    public async Task<int> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var gitRoot = FindGitRepositoryRoot(_workingDirectory);
        if (gitRoot is null)
        {
            await _output.WriteLineAsync("Error: Not inside a Git repository. Run this command from a Git repository root.");
            return 1;
        }

        var vsCodeDir = Path.Combine(gitRoot, VsCodeDir);
        var mcpConfigPath = Path.Combine(vsCodeDir, McpConfigFileName);

        if (File.Exists(mcpConfigPath))
        {
            await _output.WriteLineAsync($"MCP configuration already exists: {mcpConfigPath}");
            await _output.WriteLineAsync("Delete the file and run 'init' again to regenerate.");
            return 1;
        }

        Directory.CreateDirectory(vsCodeDir);

        var normalizedExePath = _executablePath.Replace("\\", "\\\\");
        var configContent = BuildConfigContent(normalizedExePath);

        await File.WriteAllTextAsync(mcpConfigPath, configContent, cancellationToken);

        await _output.WriteLineAsync($"Created MCP configuration: {mcpConfigPath}");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("The MCP server will be launched with --repo pointing to your workspace.");
        await _output.WriteLineAsync("Restart VS Code or reload the MCP client to pick up the new configuration.");

        return 0;
    }

    internal static string BuildConfigContent(string normalizedExePath)
    {
        return $$"""
            {
              "servers": {
                "REBUSS.Pure": {
                  "type": "stdio",
                  "command": "{{normalizedExePath}}",
                  "args": ["--repo", "${workspaceFolder}"]
                }
              }
            }
            """;
    }

    private static string? FindGitRepositoryRoot(string startDirectory)
    {
        var dir = new DirectoryInfo(startDirectory);

        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;

            dir = dir.Parent;
        }

        return null;
    }
}
