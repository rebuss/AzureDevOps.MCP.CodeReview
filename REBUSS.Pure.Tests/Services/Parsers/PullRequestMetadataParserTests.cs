using Microsoft.Extensions.Logging.Abstractions;
using REBUSS.Pure.AzureDevOps.Parsers;

namespace REBUSS.Pure.Tests.Services.Parsers;

public class PullRequestMetadataParserTests
{
    private readonly PullRequestMetadataParser _parser =
        new(NullLogger<PullRequestMetadataParser>.Instance);

    [Fact]
    public void Parse_ValidJson_ReturnsCorrectMetadata()
    {
        const string json = """
            {
                "title": "My PR",
                "status": "active",
                "sourceRefName": "refs/heads/feature/my-feature",
                "targetRefName": "refs/heads/main"
            }
            """;

        var result = _parser.Parse(json);

        Assert.Equal("My PR", result.Title);
        Assert.Equal("active", result.Status);
        Assert.Equal("feature/my-feature", result.SourceBranch);
        Assert.Equal("main", result.TargetBranch);
        Assert.Equal("refs/heads/feature/my-feature", result.SourceRefName);
        Assert.Equal("refs/heads/main", result.TargetRefName);
    }

    [Fact]
    public void Parse_RefNameWithoutRefsHeadsPrefix_ReturnsRefNameAsIs()
    {
        const string json = """
            {
                "title": "T",
                "status": "active",
                "sourceRefName": "feature/branch",
                "targetRefName": "main"
            }
            """;

        var result = _parser.Parse(json);

        Assert.Equal("feature/branch", result.SourceBranch);
        Assert.Equal("main", result.TargetBranch);
    }

    [Fact]
    public void Parse_MissingFields_ReturnsDefaults()
    {
        const string json = "{}";

        var result = _parser.Parse(json);

        Assert.Equal("Unknown", result.Title);
        Assert.Equal("Unknown", result.Status);
        Assert.Equal(string.Empty, result.SourceRefName);
        Assert.Equal(string.Empty, result.TargetRefName);
    }

    [Fact]
    public void Parse_InvalidJson_ReturnsUnknownDefaults()
    {
        var result = _parser.Parse("not-json");

        Assert.Equal("Unknown", result.Title);
        Assert.Equal("Unknown", result.Status);
        Assert.Equal("Unknown", result.SourceBranch);
        Assert.Equal("Unknown", result.TargetBranch);
    }
}
