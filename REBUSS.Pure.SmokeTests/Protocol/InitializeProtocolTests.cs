using System.Text.Json;
using REBUSS.Pure.SmokeTests.Infrastructure;

namespace REBUSS.Pure.SmokeTests.Protocol;

/// <summary>
/// MCP protocol handshake tests — no credentials required.
/// </summary>
[Collection("Protocol")]
[Trait("Category", "Protocol")]
public class InitializeProtocolTests
{
    private readonly ProtocolMcpProcessFixture _fixture;

    public InitializeProtocolTests(ProtocolMcpProcessFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Initialize_ReturnsProtocolVersion()
    {
        var response = await _fixture.Server.SendInitializeAsync();
        var result = response.RootElement.GetProperty("result");

        Assert.Equal("2024-11-05", result.GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public async Task Initialize_ReturnsServerInfo()
    {
        var response = await _fixture.Server.SendInitializeAsync();
        var result = response.RootElement.GetProperty("result");

        Assert.True(result.TryGetProperty("serverInfo", out var serverInfo));
        var name = serverInfo.GetProperty("name").GetString();
        Assert.False(string.IsNullOrWhiteSpace(name));
    }

    [Fact]
    public async Task Initialize_ReturnsCapabilities()
    {
        var response = await _fixture.Server.SendInitializeAsync();
        var result = response.RootElement.GetProperty("result");

        Assert.True(result.TryGetProperty("capabilities", out var capabilities));
        Assert.True(capabilities.TryGetProperty("tools", out _));
    }
}
