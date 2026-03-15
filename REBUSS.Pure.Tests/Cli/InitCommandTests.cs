using REBUSS.Pure.Cli;

namespace REBUSS.Pure.Tests.Cli;

public class InitCommandTests
{
    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenNotInGitRepository()
    {
        var output = new StringWriter();
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            var command = new InitCommand(output, tempDir, "rebuss-pure.exe");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(1, exitCode);
            Assert.Contains("Not inside a Git repository", output.ToString());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_CreatesVsCodeMcpJson_InGitRepositoryRoot()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var gitDir = Path.Combine(tempDir, ".git");
        Directory.CreateDirectory(gitDir);

        try
        {
            var output = new StringWriter();
            var command = new InitCommand(output, tempDir, @"C:\tools\REBUSS.Pure.exe");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);

            var mcpConfigPath = Path.Combine(tempDir, ".vscode", "mcp.json");
            Assert.True(File.Exists(mcpConfigPath));

            var content = await File.ReadAllTextAsync(mcpConfigPath);
            Assert.Contains("REBUSS.Pure", content);
            Assert.Contains("--repo", content);
            Assert.Contains("${workspaceFolder}", content);
            Assert.Contains(@"C:\\tools\\REBUSS.Pure.exe", content);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenConfigAlreadyExists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var gitDir = Path.Combine(tempDir, ".git");
        var vsCodeDir = Path.Combine(tempDir, ".vscode");
        Directory.CreateDirectory(gitDir);
        Directory.CreateDirectory(vsCodeDir);
        await File.WriteAllTextAsync(Path.Combine(vsCodeDir, "mcp.json"), "{}");

        try
        {
            var output = new StringWriter();
            var command = new InitCommand(output, tempDir, "rebuss-pure.exe");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(1, exitCode);
            Assert.Contains("already exists", output.ToString());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WorksFromSubdirectory_FindsGitRoot()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var gitDir = Path.Combine(tempDir, ".git");
        var subDir = Path.Combine(tempDir, "src", "app");
        Directory.CreateDirectory(gitDir);
        Directory.CreateDirectory(subDir);

        try
        {
            var output = new StringWriter();
            var command = new InitCommand(output, subDir, "rebuss-pure.exe");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);

            var mcpConfigPath = Path.Combine(tempDir, ".vscode", "mcp.json");
            Assert.True(File.Exists(mcpConfigPath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void BuildConfigContent_ProducesValidJsonStructure()
    {
        var content = InitCommand.BuildConfigContent("C:\\\\tools\\\\REBUSS.Pure.exe");

        Assert.Contains("\"REBUSS.Pure\"", content);
        Assert.Contains("\"stdio\"", content);
        Assert.Contains("\"--repo\"", content);
        Assert.Contains("\"${workspaceFolder}\"", content);
    }
}
