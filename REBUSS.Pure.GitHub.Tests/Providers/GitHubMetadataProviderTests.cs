using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using REBUSS.Pure.GitHub.Api;
using REBUSS.Pure.GitHub.Parsers;
using REBUSS.Pure.GitHub.Providers;
using REBUSS.Pure.Core.Exceptions;

namespace REBUSS.Pure.GitHub.Tests.Providers;

public class GitHubMetadataProviderTests
{
    private readonly IGitHubApiClient _apiClient = Substitute.For<IGitHubApiClient>();
    private readonly GitHubMetadataProvider _provider;

    private const string PrDetailsJson = """
        {
            "number": 99,
            "title": "Implement feature Y",
            "body": "Full description of feature Y",
            "state": "open",
            "draft": false,
            "user": { "login": "dev-user" },
            "created_at": "2024-06-01T12:00:00Z",
            "closed_at": null,
            "head": { "ref": "feature/y", "sha": "head123" },
            "base": {
                "ref": "main",
                "sha": "base456",
                "repo": { "name": "my-project" }
            },
            "additions": 100,
            "deletions": 20,
            "changed_files": 8,
            "commits": 5
        }
        """;

    private const string CommitsJson = """
        [
            { "sha": "aaa111" },
            { "sha": "bbb222" },
            { "sha": "ccc333" }
        ]
        """;

    public GitHubMetadataProviderTests()
    {
        var prParser = new GitHubPullRequestParser(NullLogger<GitHubPullRequestParser>.Instance);

        _provider = new GitHubMetadataProvider(
            _apiClient,
            prParser,
            NullLogger<GitHubMetadataProvider>.Instance);
    }

    [Fact]
    public async Task GetMetadataAsync_ReturnsParsedMetadata()
    {
        _apiClient.GetPullRequestDetailsAsync(99).Returns(PrDetailsJson);
        _apiClient.GetPullRequestCommitsAsync(99).Returns(CommitsJson);

        var result = await _provider.GetMetadataAsync(99);

        Assert.Equal(99, result.PullRequestId);
        Assert.Equal("Implement feature Y", result.Title);
        Assert.Equal("Full description of feature Y", result.Description);
        Assert.Equal("active", result.Status);
        Assert.Equal("dev-user", result.AuthorLogin);
        Assert.Equal("feature/y", result.SourceBranch);
        Assert.Equal("main", result.TargetBranch);
    }

    [Fact]
    public async Task GetMetadataAsync_IncludesCommitShas()
    {
        _apiClient.GetPullRequestDetailsAsync(99).Returns(PrDetailsJson);
        _apiClient.GetPullRequestCommitsAsync(99).Returns(CommitsJson);

        var result = await _provider.GetMetadataAsync(99);

        Assert.Equal(3, result.CommitShas.Count);
        Assert.Contains("aaa111", result.CommitShas);
        Assert.Contains("bbb222", result.CommitShas);
        Assert.Contains("ccc333", result.CommitShas);
    }

    [Fact]
    public async Task GetMetadataAsync_IncludesStatistics()
    {
        _apiClient.GetPullRequestDetailsAsync(99).Returns(PrDetailsJson);
        _apiClient.GetPullRequestCommitsAsync(99).Returns(CommitsJson);

        var result = await _provider.GetMetadataAsync(99);

        Assert.Equal(100, result.Additions);
        Assert.Equal(20, result.Deletions);
        Assert.Equal(8, result.ChangedFilesCount);
    }

    [Fact]
    public async Task GetMetadataAsync_HandlesEmptyCommitsArray()
    {
        _apiClient.GetPullRequestDetailsAsync(99).Returns(PrDetailsJson);
        _apiClient.GetPullRequestCommitsAsync(99).Returns("[]");

        var result = await _provider.GetMetadataAsync(99);

        Assert.Empty(result.CommitShas);
    }

    [Fact]
    public async Task GetMetadataAsync_Throws_WhenPrNotFound()
    {
        _apiClient.GetPullRequestDetailsAsync(404)
            .ThrowsAsync(new HttpRequestException("Not Found", null, HttpStatusCode.NotFound));

        await Assert.ThrowsAsync<PullRequestNotFoundException>(
            () => _provider.GetMetadataAsync(404));
    }

    [Fact]
    public async Task GetMetadataAsync_HandlesInvalidCommitsJson()
    {
        _apiClient.GetPullRequestDetailsAsync(99).Returns(PrDetailsJson);
        _apiClient.GetPullRequestCommitsAsync(99).Returns("not-json");

        var result = await _provider.GetMetadataAsync(99);

        Assert.Empty(result.CommitShas);
    }
}
