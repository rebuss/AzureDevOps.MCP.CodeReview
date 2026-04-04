using Microsoft.Extensions.Logging.Abstractions;
using REBUSS.Pure.AzureDevOps.Parsers;

namespace REBUSS.Pure.AzureDevOps.Tests.Parsers;

public class FileChangesParserTests
{
    private readonly FileChangesParser _parser =
        new(NullLogger<FileChangesParser>.Instance);

    [Fact]
    public void Parse_ChangeEntriesProperty_ReturnsFiles()
    {
        const string json = """
            {
                "changeEntries": [
                    { "changeType": "edit", "item": { "path": "/src/A.cs" } },
                    { "changeType": "add",  "item": { "path": "/src/B.cs" } }
                ]
            }
            """;

        var result = _parser.Parse(json);

        Assert.Equal(2, result.Count);
        Assert.Equal("src/A.cs", result[0].Path);
        Assert.Equal("edit", result[0].ChangeType);
        Assert.Equal("src/B.cs", result[1].Path);
        Assert.Equal("add", result[1].ChangeType);
    }

    [Fact]
    public void Parse_ChangesProperty_ReturnsFiles()
    {
        const string json = """
            {
                "changes": [
                    { "changeType": "delete", "item": { "path": "/old/File.cs" } }
                ]
            }
            """;

        var result = _parser.Parse(json);

        Assert.Single(result);
        Assert.Equal("old/File.cs", result[0].Path);
        Assert.Equal("delete", result[0].ChangeType);
    }

    [Fact]
    public void Parse_FolderEntries_AreSkipped()
    {
        const string json = """
            {
                "changeEntries": [
                    { "changeType": "edit", "item": { "path": "/src/",    "isFolder": true } },
                    { "changeType": "edit", "item": { "path": "/src/A.cs" } }
                ]
            }
            """;

        var result = _parser.Parse(json);

        Assert.Single(result);
        Assert.Equal("src/A.cs", result[0].Path);
    }

    [Fact]
    public void Parse_EntryWithNoItemProperty_IsSkipped()
    {
        const string json = """
            {
                "changeEntries": [
                    { "changeType": "edit" },
                    { "changeType": "add", "item": { "path": "/src/A.cs" } }
                ]
            }
            """;

        var result = _parser.Parse(json);

        Assert.Single(result);
        Assert.Equal("src/A.cs", result[0].Path);
    }

    [Fact]
    public void Parse_EmptyJson_ReturnsEmptyList()
    {
        var result = _parser.Parse("{}");

        Assert.Empty(result);
    }

    [Fact]
    public void Parse_InvalidJson_ReturnsEmptyList()
    {
        var result = _parser.Parse("not-json");

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("add",    "add")]
    [InlineData("edit",   "edit")]
    [InlineData("delete", "delete")]
    [InlineData("rename", "rename")]
    [InlineData("ADD",    "add")]
    [InlineData("EDIT",   "edit")]
    [InlineData("unknown", "unknown")]
    public void Parse_ChangeType_IsMappedCorrectly(string input, string expected)
    {
        var json = $$"""
            {
                "changeEntries": [
                    { "changeType": "{{input}}", "item": { "path": "/f.cs" } }
                ]
            }
            """;

        var result = _parser.Parse(json);

        Assert.Single(result);
        Assert.Equal(expected, result[0].ChangeType);
    }
}
