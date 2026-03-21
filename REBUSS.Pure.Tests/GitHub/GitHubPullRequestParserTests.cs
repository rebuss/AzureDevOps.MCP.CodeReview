using REBUSS.Pure.GitHub.Parsers;

namespace REBUSS.Pure.Tests.GitHub;

public class GitHubPullRequestParserTests
{
    private readonly GitHubPullRequestParser _parser = new(
        Microsoft.Extensions.Logging.Abstractions.NullLogger<GitHubPullRequestParser>.Instance);

    private const string FullPrJson = """
        {
            "number": 123,
            "title": "Add feature X",
            "body": "This PR adds feature X for better performance.",
            "state": "open",
            "draft": true,
            "user": { "login": "octocat" },
            "created_at": "2024-01-15T10:30:00Z",
            "closed_at": null,
            "head": { "ref": "feature/x", "sha": "abc123def456" },
            "base": {
                "ref": "main",
                "sha": "789ghi012jkl",
                "repo": { "name": "my-repo" }
            },
            "additions": 42,
            "deletions": 10,
            "changed_files": 5,
            "commits": 3
        }
        """;

    [Fact]
    public void Parse_ExtractsLightweightMetadata()
    {
        var result = _parser.Parse(FullPrJson);

        Assert.Equal("Add feature X", result.Title);
        Assert.Equal("active", result.Status);
        Assert.Equal("feature/x", result.SourceBranch);
        Assert.Equal("main", result.TargetBranch);
        Assert.Equal("refs/heads/feature/x", result.SourceRefName);
        Assert.Equal("refs/heads/main", result.TargetRefName);
    }

    [Fact]
    public void ParseFull_ExtractsAllMetadata()
    {
        var result = _parser.ParseFull(FullPrJson);

        Assert.Equal(123, result.PullRequestId);
        Assert.Equal("Add feature X", result.Title);
        Assert.Equal("This PR adds feature X for better performance.", result.Description);
        Assert.Equal("active", result.Status);
        Assert.True(result.IsDraft);
        Assert.Equal("octocat", result.AuthorLogin);
        Assert.Equal("feature/x", result.SourceBranch);
        Assert.Equal("main", result.TargetBranch);
        Assert.Equal("abc123def456", result.LastMergeSourceCommitId);
        Assert.Equal("789ghi012jkl", result.LastMergeTargetCommitId);
        Assert.Equal("my-repo", result.RepositoryName);
        Assert.Equal(42, result.Additions);
        Assert.Equal(10, result.Deletions);
        Assert.Equal(5, result.ChangedFilesCount);
        Assert.Null(result.ClosedDate);
    }

    [Fact]
    public void ParseBaseCommit_ExtractsBaseSha()
    {
        var sha = _parser.ParseBaseCommit(FullPrJson);

        Assert.Equal("789ghi012jkl", sha);
    }

    [Fact]
    public void ParseHeadCommit_ExtractsHeadSha()
    {
        var sha = _parser.ParseHeadCommit(FullPrJson);

        Assert.Equal("abc123def456", sha);
    }

    [Fact]
    public void Parse_HandlesClosedState()
    {
        var json = """
            {
                "number": 1,
                "title": "Closed PR",
                "state": "closed",
                "head": { "ref": "fix", "sha": "aaa" },
                "base": { "ref": "main", "sha": "bbb" }
            }
            """;

        var result = _parser.Parse(json);

        Assert.Equal("completed", result.Status);
    }

    [Fact]
    public void Parse_HandlesInvalidJson_ReturnsFallbacks()
    {
        var result = _parser.Parse("invalid json{{{");

        Assert.Equal("Unknown", result.Title);
        Assert.Equal("Unknown", result.Status);
    }

    [Fact]
    public void ParseFull_HandlesInvalidJson_ReturnsFallbacks()
    {
        var result = _parser.ParseFull("invalid json{{{");

        Assert.Equal("Unknown", result.Title);
        Assert.Equal("Unknown", result.Status);
    }

    [Fact]
    public void MapState_MapsOpenToActive()
    {
        Assert.Equal("active", GitHubPullRequestParser.MapState("open"));
    }

    [Fact]
    public void MapState_MapsClosedToCompleted()
    {
        Assert.Equal("completed", GitHubPullRequestParser.MapState("closed"));
    }

    [Fact]
    public void MapState_PreservesUnknownState()
    {
        Assert.Equal("merged", GitHubPullRequestParser.MapState("merged"));
    }
}
