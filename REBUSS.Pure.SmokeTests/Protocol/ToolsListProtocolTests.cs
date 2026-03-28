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
        "get_pr_diff",
        "get_file_diff",
        "get_pr_metadata",
        "get_pr_files",
        "get_file_content_at_ref",
        "get_local_files",
        "get_local_file_diff"
    ];

    private static readonly string[] PrNumberTools =
    [
        "get_file_diff",
        "get_pr_metadata"
    ];

    /// <summary>
    /// Tools that support pagination (Feature 004). prNumber/scope is optional when pageReference is used.
    /// </summary>
    private static readonly string[] PaginationEnabledTools =
    [
        "get_pr_diff",
        "get_pr_files",
        "get_local_files"
    ];

    private readonly ProtocolMcpProcessFixture _fixture;

    public ToolsListProtocolTests(ProtocolMcpProcessFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ToolsList_ReturnsAllSevenTools()
    {
        var response = await _fixture.Server.SendToolsListAsync();
        var tools = response.RootElement
            .GetProperty("result")
            .GetProperty("tools");

        Assert.Equal(7, tools.GetArrayLength());
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
    /// Feature 004: get_local_file_diff schema is unchanged (excluded from pagination).
    /// </summary>
    [Fact]
    public async Task ToolsList_GetLocalFileDiff_SchemaUnchanged()
    {
        var response = await _fixture.Server.SendToolsListAsync();
        var tools = response.RootElement
            .GetProperty("result")
            .GetProperty("tools");

        var toolMap = tools.EnumerateArray()
            .ToDictionary(
                t => t.GetProperty("name").GetString()!,
                t => t);

        var schema = toolMap["get_local_file_diff"].GetProperty("inputSchema");
        var props = schema.GetProperty("properties");

        Assert.False(props.TryGetProperty("pageReference", out _),
            "get_local_file_diff should not have pageReference (excluded from pagination).");
        Assert.False(props.TryGetProperty("pageNumber", out _),
            "get_local_file_diff should not have pageNumber (excluded from pagination).");
    }
}
