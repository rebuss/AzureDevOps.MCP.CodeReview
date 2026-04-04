using REBUSS.Pure.SmokeTests.Fixtures;

namespace REBUSS.Pure.SmokeTests.McpProtocol;

/// <summary>
/// Smoke tests that verify the MCP server process responds correctly to JSON-RPC messages.
/// Each test starts the real server as a child process over stdio.
/// </summary>
[Trait("Category", "Smoke")]
public class McpServerSmokeTests
{
    private static readonly string[] ExpectedTools =
    [
        "get_pr_metadata",
        "get_pr_files",
        "get_file_content_at_ref",
        "get_local_files"
    ];

    [Fact]
    public async Task Initialize_ReturnsServerInfo()
    {
        using var repo = TempGitRepoFixture.Create("https://github.com/fake/repo.git");
        await using var server = McpProcessFixture.Start(repo.RootPath);

        var response = await server.InitializeHandshakeAsync();

        var result = response.RootElement.GetProperty("result");
        Assert.True(result.TryGetProperty("serverInfo", out var serverInfo));
        Assert.Equal("REBUSS.Pure", serverInfo.GetProperty("name").GetString());
        Assert.True(result.TryGetProperty("capabilities", out _));

        await server.ShutdownAsync();
    }

    [Fact]
    public async Task ToolsList_ReturnsAllExpectedTools()
    {
        using var repo = TempGitRepoFixture.Create("https://github.com/fake/repo.git");
        await using var server = McpProcessFixture.Start(repo.RootPath);

        await server.InitializeHandshakeAsync("1");

        var toolsResponse = await server.SendAsync(new
        {
            jsonrpc = "2.0",
            id = "2",
            method = "tools/list"
        });

        var toolsResult = toolsResponse.RootElement.GetProperty("result");
        var tools = toolsResult.GetProperty("tools");

        var toolNames = tools.EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToList();

        foreach (var expected in ExpectedTools)
        {
            Assert.Contains(expected, toolNames);
        }

        await server.ShutdownAsync();
    }

    [Fact]
    public async Task ToolsList_EachToolHasInputSchema()
    {
        using var repo = TempGitRepoFixture.Create("https://github.com/fake/repo.git");
        await using var server = McpProcessFixture.Start(repo.RootPath);

        await server.InitializeHandshakeAsync("1");

        var toolsResponse = await server.SendAsync(new
        {
            jsonrpc = "2.0",
            id = "2",
            method = "tools/list"
        });

        var tools = toolsResponse.RootElement
            .GetProperty("result")
            .GetProperty("tools");

        foreach (var tool in tools.EnumerateArray())
        {
            var name = tool.GetProperty("name").GetString();
            Assert.True(tool.TryGetProperty("inputSchema", out var schema),
                $"Tool '{name}' is missing inputSchema.");
            Assert.Equal("object", schema.GetProperty("type").GetString());
        }

        await server.ShutdownAsync();
    }

    [Fact]
    public async Task GetLocalFiles_ReturnsValidResponse()
    {
        using var repo = TempGitRepoFixture.Create("https://github.com/fake/repo.git");

        // Create an initial commit so HEAD exists
        repo.CreateFile("dummy.txt", "hello");
        RunGitInRepo(repo.RootPath, "add .");
        RunGitInRepo(repo.RootPath, "commit -m \"initial\"");

        await using var server = McpProcessFixture.Start(repo.RootPath);

        await server.InitializeHandshakeAsync("1");

        // Call get_local_files
        var response = await server.SendAsync(new
        {
            jsonrpc = "2.0",
            id = "2",
            method = "tools/call",
            @params = new { name = "get_local_files", arguments = new { scope = "working-tree" } }
        });

        var result = response.RootElement.GetProperty("result");
        var isError = result.TryGetProperty("isError", out var errProp) && errProp.GetBoolean();
        Assert.False(isError,
            $"get_local_files returned error: {result}");

        await server.ShutdownAsync();
    }

    [Fact]
    public async Task GetLocalFiles_WithChangedFile_ReturnsDiff()
    {
        using var repo = TempGitRepoFixture.Create("https://github.com/fake/repo.git");

        // Create initial commit, then modify a file
        repo.CreateFile("test.txt", "original content");
        RunGitInRepo(repo.RootPath, "add .");
        RunGitInRepo(repo.RootPath, "commit -m \"initial\"");
        repo.CreateFile("test.txt", "modified content");

        await using var server = McpProcessFixture.Start(repo.RootPath);

        await server.InitializeHandshakeAsync("1");

        var response = await server.SendAsync(new
        {
            jsonrpc = "2.0",
            id = "2",
            method = "tools/call",
            @params = new { name = "get_local_files", arguments = new { scope = "working-tree" } }
        });

        var result = response.RootElement.GetProperty("result");
        var isError = result.TryGetProperty("isError", out var errProp) && errProp.GetBoolean();
        Assert.False(isError);

        // Tool responses are plain text blocks; verify the changed file appears in output.
        var contentText = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("test.txt", contentText, StringComparison.OrdinalIgnoreCase);

        await server.ShutdownAsync();
    }

    [Fact]
    public async Task UnknownMethod_ReturnsJsonRpcError()
    {
        using var repo = TempGitRepoFixture.Create("https://github.com/fake/repo.git");
        await using var server = McpProcessFixture.Start(repo.RootPath);

        await server.InitializeHandshakeAsync("1");

        var response = await server.SendAsync(new
        {
            jsonrpc = "2.0",
            id = "err-1",
            method = "nonexistent/method"
        });

        Assert.True(response.RootElement.TryGetProperty("error", out var error),
            "Expected a JSON-RPC error response for unknown method.");
        Assert.Equal(-32601, error.GetProperty("code").GetInt32());

        await server.ShutdownAsync();
    }

    [Fact]
    public async Task GracefulShutdown_OnStdinClose_ProcessExitsCleanly()
    {
        using var repo = TempGitRepoFixture.Create("https://github.com/fake/repo.git");
        await using var server = McpProcessFixture.Start(repo.RootPath);

        await server.InitializeHandshakeAsync("1");

        // Close stdin — server should exit gracefully
        var exitCode = await server.ShutdownAsync(timeout: TimeSpan.FromSeconds(10));

        Assert.Equal(0, exitCode);
    }

    private static void RunGitInRepo(string repoPath, string arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = System.Diagnostics.Process.Start(psi)!;
        process.WaitForExit(TimeSpan.FromSeconds(10));
    }
}
