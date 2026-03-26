using REBUSS.Pure.GitHub.Parsers;

namespace REBUSS.Pure.GitHub.Tests.Parsers;

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
    public void Parse_HandlesClosedState_WithoutMergedField_MapsToAbandoned()
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

        Assert.Equal("abandoned", result.Status);
    }

    [Fact]
    public void Parse_HandlesClosedState_WithMergedTrue_MapsToCompleted()
    {
        var json = """
            {
                "number": 1,
                "title": "Merged PR",
                "state": "closed",
                "merged": true,
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
    public void ParseWithCommits_ExtractsMetadataAndCommitShas()
    {
        var (metadata, baseCommit, headCommit) = _parser.ParseWithCommits(FullPrJson);

        Assert.Equal("Add feature X", metadata.Title);
        Assert.Equal("active", metadata.Status);
        Assert.Equal("feature/x", metadata.SourceBranch);
        Assert.Equal("main", metadata.TargetBranch);
        Assert.Equal("789ghi012jkl", baseCommit);
        Assert.Equal("abc123def456", headCommit);
    }

    [Fact]
    public void ParseWithCommits_HandlesInvalidJson_ReturnsFallbacks()
    {
        var (metadata, baseCommit, headCommit) = _parser.ParseWithCommits("invalid json{{{");

        Assert.Equal("Unknown", metadata.Title);
        Assert.Equal("Unknown", metadata.Status);
        Assert.Equal(string.Empty, baseCommit);
        Assert.Equal(string.Empty, headCommit);
    }

    [Fact]
    public void ParseWithCommits_MergedPr_ReturnsCompleted()
    {
        var json = """
            {
                "number": 5,
                "title": "Merged PR",
                "state": "closed",
                "merged": true,
                "head": { "ref": "feature", "sha": "head123" },
                "base": { "ref": "main", "sha": "base456" }
            }
            """;

        var (metadata, _, _) = _parser.ParseWithCommits(json);

        Assert.Equal("completed", metadata.Status);
    }

    [Fact]
    public void MapState_MapsOpenToActive()
    {
        Assert.Equal("active", GitHubPullRequestParser.MapState("open", merged: false));
    }

    [Fact]
    public void MapState_MapsClosedWithMergedToCompleted()
    {
        Assert.Equal("completed", GitHubPullRequestParser.MapState("closed", merged: true));
    }

    [Fact]
    public void MapState_MapsClosedWithoutMergedToAbandoned()
    {
        Assert.Equal("abandoned", GitHubPullRequestParser.MapState("closed", merged: false));
    }

    [Fact]
    public void MapState_PreservesUnknownState()
    {
        Assert.Equal("merged", GitHubPullRequestParser.MapState("merged", merged: false));
    }
}
