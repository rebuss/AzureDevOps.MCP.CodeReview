using Microsoft.Extensions.Logging.Abstractions;
using REBUSS.Pure.AzureDevOps.Parsers;

namespace REBUSS.Pure.Tests.Services.Parsers;

public class IterationInfoParserTests
{
    private readonly IterationInfoParser _parser =
        new(NullLogger<IterationInfoParser>.Instance);

    [Fact]
    public void ParseLast_ValidJson_ReturnsLastIterationInfo()
    {
        const string json = """
            {
                "value": [
                    { "id": 1, "sourceRefCommit": { "commitId": "aaa" }, "commonRefCommit": { "commitId": "bbb" } },
                    { "id": 2, "sourceRefCommit": { "commitId": "ccc" }, "commonRefCommit": { "commitId": "ddd" } }
                ]
            }
            """;

        var result = _parser.ParseLast(json);

        Assert.Equal(2, result.Id);
        Assert.Equal("ddd", result.BaseCommit);
        Assert.Equal("ccc", result.TargetCommit);
    }

    [Fact]
    public void ParseLast_NoCommonRefCommit_FallsBackToTargetRefCommit()
    {
        const string json = """
            {
                "value": [
                    {
                        "id": 1,
                        "sourceRefCommit": { "commitId": "aaa" },
                        "targetRefCommit": { "commitId": "fallback" }
                    }
                ]
            }
            """;

        var result = _parser.ParseLast(json);

        Assert.Equal("fallback", result.BaseCommit);
        Assert.Equal("aaa", result.TargetCommit);
    }

    [Fact]
    public void ParseLast_EmptyArray_ReturnsEmpty()
    {
        var result = _parser.ParseLast("""{"value":[]}""");

        Assert.Equal(0, result.Id);
        Assert.Equal(string.Empty, result.BaseCommit);
        Assert.Equal(string.Empty, result.TargetCommit);
    }

    [Fact]
    public void ParseLast_MissingValueProperty_ReturnsEmpty()
    {
        var result = _parser.ParseLast("{}");

        Assert.Equal(0, result.Id);
        Assert.Equal(string.Empty, result.BaseCommit);
    }

    [Fact]
    public void ParseLast_InvalidJson_ReturnsEmpty()
    {
        var result = _parser.ParseLast("not-json");

        Assert.Equal(0, result.Id);
        Assert.Equal(string.Empty, result.BaseCommit);
        Assert.Equal(string.Empty, result.TargetCommit);
    }

    [Fact]
    public void ParseLast_IterationWithNoCommitProperties_ReturnsEmptyCommits()
    {
        const string json = """{"value":[{"id":5}]}""";

        var result = _parser.ParseLast(json);

        Assert.Equal(5, result.Id);
        Assert.Equal(string.Empty, result.BaseCommit);
        Assert.Equal(string.Empty, result.TargetCommit);
    }
}
