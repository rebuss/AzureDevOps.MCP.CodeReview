using Microsoft.Extensions.Logging.Abstractions;
using REBUSS.Pure.GitHub.Parsers;

namespace REBUSS.Pure.GitHub.Tests.Parsers;

public class GitHubFileChangesParserTests
{
    private readonly GitHubFileChangesParser _parser =
        new(NullLogger<GitHubFileChangesParser>.Instance);

    [Fact]
    public void Parse_ReturnsFiles()
    {
        const string json = """
            [
                { "filename": "src/A.cs", "status": "modified", "additions": 5, "deletions": 2 },
                { "filename": "src/B.cs", "status": "added",    "additions": 10, "deletions": 0 }
            ]
            """;

        var result = _parser.Parse(json);

        Assert.Equal(2, result.Count);
        Assert.Equal("src/A.cs", result[0].Path);
        Assert.Equal("edit", result[0].ChangeType);
        Assert.Equal(5, result[0].Additions);
        Assert.Equal(2, result[0].Deletions);
        Assert.Equal("src/B.cs", result[1].Path);
        Assert.Equal("add", result[1].ChangeType);
        Assert.Equal(10, result[1].Additions);
        Assert.Equal(0, result[1].Deletions);
    }

    [Fact]
    public void Parse_MissingAdditionsDeletions_DefaultsToZero()
    {
        const string json = """
            [
                { "filename": "src/C.cs", "status": "modified" }
            ]
            """;

        var result = _parser.Parse(json);

        Assert.Single(result);
        Assert.Equal(0, result[0].Additions);
        Assert.Equal(0, result[0].Deletions);
    }

    [Fact]
    public void Parse_SkipsEntryWithEmptyFilename()
    {
        const string json = """
            [
                { "filename": "", "status": "modified" },
                { "filename": "src/A.cs", "status": "added" }
            ]
            """;

        var result = _parser.Parse(json);

        Assert.Single(result);
        Assert.Equal("src/A.cs", result[0].Path);
    }

    [Fact]
    public void Parse_EmptyArray_ReturnsEmptyList()
    {
        var result = _parser.Parse("[]");

        Assert.Empty(result);
    }

    [Fact]
    public void Parse_NonArrayJson_ReturnsEmptyList()
    {
        var result = _parser.Parse("""{ "key": "value" }""");

        Assert.Empty(result);
    }

    [Fact]
    public void Parse_InvalidJson_ReturnsEmptyList()
    {
        var result = _parser.Parse("not-json");

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("added", "add")]
    [InlineData("removed", "delete")]
    [InlineData("modified", "edit")]
    [InlineData("renamed", "rename")]
    [InlineData("copied", "add")]
    [InlineData("changed", "edit")]
    [InlineData("ADDED", "add")]
    [InlineData("MODIFIED", "edit")]
    [InlineData("unchanged", "unchanged")]
    public void MapStatus_MapsCorrectly(string input, string expected)
    {
        var mapped = GitHubFileChangesParser.MapStatus(input);

        Assert.Equal(expected, mapped);
    }

    [Fact]
    public void ParseWithPatches_ExtractsPatchField_WhenPresent()
    {
        const string json = """
            [
                {
                    "filename": "src/A.cs",
                    "status": "modified",
                    "additions": 1,
                    "deletions": 1,
                    "patch": "@@ -1,1 +1,1 @@\n-old\n+new"
                }
            ]
            """;

        var result = _parser.ParseWithPatches(json);

        Assert.Single(result);
        Assert.Equal("src/A.cs", result[0].File.Path);
        Assert.Equal("@@ -1,1 +1,1 @@\n-old\n+new", result[0].Patch);
    }

    [Fact]
    public void ParseWithPatches_ReturnsNullPatch_WhenAbsent()
    {
        const string json = """
            [
                { "filename": "lib/blob.bin", "status": "modified", "additions": 0, "deletions": 0 }
            ]
            """;

        var result = _parser.ParseWithPatches(json);

        Assert.Single(result);
        Assert.Null(result[0].Patch);
    }
}
