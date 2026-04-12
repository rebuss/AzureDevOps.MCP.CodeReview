using REBUSS.Pure.SmokeTests.Fixtures;

namespace REBUSS.Pure.SmokeTests.McpProtocol;

/// <summary>
/// Protocol-level smoke tests for feature 018 — Copilot review auth gating.
/// <para>
/// Intentionally minimal: the smoke harness cannot deterministically control
/// <c>gh auth login</c> state on arbitrary dev/CI machines, so these tests only
/// assert the MCP server survives startup and <c>tools/list</c> with the new
/// verification plumbing wired in. Full end-to-end coverage of the
/// unauthenticated fallback path is handled by the manual quickstart run in
/// tasks.md T035 — see the Notes section there for the rationale.
/// </para>
/// </summary>
[Trait("Category", "Smoke")]
public class CopilotReviewAuthGatingSmokeTests
{
    [Fact]
    public async Task Server_WithCopilotReviewEnabled_StartsAndInitializes()
    {
        // CopilotReview:Enabled defaults to true in appsettings.json. Starting the
        // server must succeed regardless of gh auth state — the verification is
        // lazy and does not fire at startup.
        using var repo = TempGitRepoFixture.Create("https://github.com/fake/repo.git");
        await using var server = McpProcessFixture.Start(repo.RootPath);

        var response = await server.InitializeHandshakeAsync();

        var result = response.RootElement.GetProperty("result");
        Assert.True(result.TryGetProperty("serverInfo", out var serverInfo));
        Assert.Equal("REBUSS.Pure", serverInfo.GetProperty("name").GetString());

        await server.ShutdownAsync();
    }

    [Fact]
    public async Task Server_WithCopilotReviewEnabled_ToolsListStillReturnsAllTools()
    {
        // Regression guard: the verification runner and the availability detector
        // changes in US1 must not affect the tools/list output.
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

        var toolNames = tools.EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToList();

        Assert.Contains("get_pr_content", toolNames);
        Assert.Contains("get_pr_metadata", toolNames);
        Assert.Contains("get_local_files", toolNames);

        await server.ShutdownAsync();
    }

    [Fact]
    public async Task Server_GracefulShutdown_AfterCopilotReviewWired()
    {
        // Ensure the new CopilotVerificationRunner + its DI wiring don't block
        // the graceful shutdown hook.
        using var repo = TempGitRepoFixture.Create("https://github.com/fake/repo.git");
        await using var server = McpProcessFixture.Start(repo.RootPath);

        await server.InitializeHandshakeAsync("1");
        var exitCode = await server.ShutdownAsync(timeout: TimeSpan.FromSeconds(10));

        Assert.Equal(0, exitCode);
    }
}
