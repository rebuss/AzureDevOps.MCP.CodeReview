using System.Text.Json;
using REBUSS.Pure.SmokeTests.Infrastructure;

namespace REBUSS.Pure.SmokeTests.Protocol;

/// <summary>
/// Validates the <c>tools/list</c> response structure — no credentials required.
/// </summary>
[Collection("Protocol")]
[Trait("Category", "Protocol")]
public class ToolsListProtocolTests
{
    private static readonly string[] ExpectedToolNames =
    [
        "get_pr_metadata",
        "get_pr_files",
        "get_local_files",
        "get_pr_content",
        "get_local_content"
    ];

    private static readonly string[] PrNumberTools =
    [
        "get_pr_metadata",
        "get_pr_content"
    ];

    /// <summary>
    /// Tools that support pagination (Feature 004). prNumber/scope is optional when pageReference is used.
    /// </summary>
    private static readonly string[] PaginationEnabledTools =
    [
        "get_pr_files",
        "get_local_files"
    ];

    private readonly ProtocolMcpProcessFixture _fixture;

    public ToolsListProtocolTests(ProtocolMcpProcessFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ToolsList_ReturnsAllElevenTools()
    {
        var response = await _fixture.Server.SendToolsListAsync();
        var tools = response.RootElement
            .GetProperty("result")
            .GetProperty("tools");

        // Five legacy + four review-session (012) + two review-memory (013) tools.
        Assert.Equal(11, tools.GetArrayLength());
    }

    [Fact]
    public async Task ToolsList_EachToolHasNameAndSchema()
    {
        var response = await _fixture.Server.SendToolsListAsync();
        var tools = response.RootElement
            .GetProperty("result")
            .GetProperty("tools");

        foreach (var tool in tools.EnumerateArray())
        {
            var name = tool.GetProperty("name").GetString();
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.True(tool.TryGetProperty("description", out _), $"Tool '{name}' is missing description.");
            Assert.True(tool.TryGetProperty("inputSchema", out var schema), $"Tool '{name}' is missing inputSchema.");
            Assert.Equal("object", schema.GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task ToolsList_ContainsExpectedToolNames()
    {
        var response = await _fixture.Server.SendToolsListAsync();
        var tools = response.RootElement
            .GetProperty("result")
            .GetProperty("tools");

        var toolNames = tools.EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToHashSet();

        foreach (var expected in ExpectedToolNames)
        {
            Assert.Contains(expected, toolNames);
        }
    }

    [Fact]
    public async Task ToolsList_PrNumberToolsDeclarePrNumberProperty()
    {
        var response = await _fixture.Server.SendToolsListAsync();
        var tools = response.RootElement
            .GetProperty("result")
            .GetProperty("tools");

        var toolMap = tools.EnumerateArray()
            .ToDictionary(
                t => t.GetProperty("name").GetString()!,
                t => t);

        foreach (var prTool in PrNumberTools)
        {
            Assert.True(toolMap.ContainsKey(prTool), $"Tool '{prTool}' not found.");

            var schema = toolMap[prTool].GetProperty("inputSchema");
            Assert.True(schema.TryGetProperty("properties", out var props),
                $"Tool '{prTool}' has no 'properties' in schema.");

            Assert.True(props.TryGetProperty("prNumber", out _),
                $"Tool '{prTool}' is missing 'prNumber' property.");
        }
    }

    /// <summary>
    /// Feature 004 (SC-008): Pagination-enabled tools must declare pageReference and pageNumber properties.
    /// </summary>
    [Fact]
    public async Task ToolsList_PaginationEnabledTools_HavePageReferenceAndPageNumber()
    {
        var response = await _fixture.Server.SendToolsListAsync();
        var tools = response.RootElement
            .GetProperty("result")
            .GetProperty("tools");

        var toolMap = tools.EnumerateArray()
            .ToDictionary(
                t => t.GetProperty("name").GetString()!,
                t => t);

        foreach (var pagTool in PaginationEnabledTools)
        {
            Assert.True(toolMap.ContainsKey(pagTool), $"Tool '{pagTool}' not found.");

            var schema = toolMap[pagTool].GetProperty("inputSchema");
            Assert.True(schema.TryGetProperty("properties", out var props),
                $"Tool '{pagTool}' has no 'properties' in schema.");

            Assert.True(props.TryGetProperty("pageReference", out _),
                $"Tool '{pagTool}' is missing 'pageReference' property.");
            Assert.True(props.TryGetProperty("pageNumber", out _),
                $"Tool '{pagTool}' is missing 'pageNumber' property.");
        }
    }

    /// <summary>
    /// Feature 003: New content tools must declare pageNumber property.
    /// </summary>
    [Fact]
    public async Task ToolsList_ContentTools_HavePageNumberProperty()
    {
        var response = await _fixture.Server.SendToolsListAsync();
        var tools = response.RootElement
            .GetProperty("result")
            .GetProperty("tools");

        var toolMap = tools.EnumerateArray()
            .ToDictionary(
                t => t.GetProperty("name").GetString()!,
                t => t);

        foreach (var toolName in new[] { "get_pr_content", "get_local_content" })
        {
            Assert.True(toolMap.ContainsKey(toolName), $"Tool '{toolName}' not found.");

            var schema = toolMap[toolName].GetProperty("inputSchema");
            Assert.True(schema.TryGetProperty("properties", out var props),
                $"Tool '{toolName}' has no 'properties' in schema.");

            Assert.True(props.TryGetProperty("pageNumber", out _),
                $"Tool '{toolName}' is missing 'pageNumber' property.");
        }
    }
}
