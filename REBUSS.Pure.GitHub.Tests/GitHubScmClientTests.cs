using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using REBUSS.Pure.GitHub;
using REBUSS.Pure.GitHub.Api;
using REBUSS.Pure.GitHub.Configuration;
using REBUSS.Pure.GitHub.Parsers;
using REBUSS.Pure.GitHub.Providers;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Shared;

namespace REBUSS.Pure.GitHub.Tests;

public class GitHubScmClientTests
{
    private readonly IGitHubApiClient _apiClient = Substitute.For<IGitHubApiClient>();
    private readonly GitHubScmClient _client;

    private const string PrDetailsJson = """
        {
            "number": 7,
            "title": "Add readme",
            "state": "open",
            "draft": false,
            "head": { "ref": "docs/readme", "sha": "head111" },
            "base": { "ref": "main", "sha": "base222" },
            "user": { "login": "contributor" }
        }
        """;

    private const string CommitsJson = """
        [
            { "sha": "commit1" },
            { "sha": "commit2" }
        ]
        """;

    public GitHubScmClientTests()
    {
        var options = Options.Create(new GitHubOptions
        {
            Owner = "test-owner",
            RepositoryName = "test-repo"
        });

        var prParser = new GitHubPullRequestParser(NullLogger<GitHubPullRequestParser>.Instance);
        var changesParser = new GitHubFileChangesParser(NullLogger<GitHubFileChangesParser>.Instance);
        var diffBuilder = new StructuredDiffBuilder(new DiffPlexDiffAlgorithm(), NullLogger<StructuredDiffBuilder>.Instance);
        var fileClassifier = new FileClassifier();

        var diffProvider = new GitHubDiffProvider(
            _apiClient, prParser, changesParser, diffBuilder, fileClassifier,
            NullLogger<GitHubDiffProvider>.Instance);

        var metadataProvider = new GitHubMetadataProvider(
            _apiClient, prParser,
            NullLogger<GitHubMetadataProvider>.Instance);

        var filesProvider = new GitHubFilesProvider(
            _apiClient, changesParser, fileClassifier,
            NullLogger<GitHubFilesProvider>.Instance);

        var archiveProvider = new GitHubRepositoryArchiveProvider(_apiClient);

        _client = new GitHubScmClient(
            diffProvider, metadataProvider, filesProvider, archiveProvider, options);
    }

    [Fact]
    public void ProviderName_ReturnsGitHub()
    {
        Assert.Equal("GitHub", _client.ProviderName);
    }

    [Fact]
    public async Task GetMetadataAsync_EnrichesWebUrl()
    {
        _apiClient.GetPullRequestDetailsAsync(7).Returns(PrDetailsJson);
        _apiClient.GetPullRequestCommitsAsync(7).Returns(CommitsJson);

        var metadata = await _client.GetMetadataAsync(7);

        Assert.Equal("https://github.com/test-owner/test-repo/pull/7", metadata.WebUrl);
    }

    [Fact]
    public async Task GetMetadataAsync_EnrichesRepositoryFullName()
    {
        _apiClient.GetPullRequestDetailsAsync(7).Returns(PrDetailsJson);
        _apiClient.GetPullRequestCommitsAsync(7).Returns(CommitsJson);

        var metadata = await _client.GetMetadataAsync(7);

        Assert.Equal("test-owner/test-repo", metadata.RepositoryFullName);
    }

    [Fact]
    public async Task GetMetadataAsync_DelegatesToMetadataProvider()
    {
        _apiClient.GetPullRequestDetailsAsync(7).Returns(PrDetailsJson);
        _apiClient.GetPullRequestCommitsAsync(7).Returns(CommitsJson);

        var metadata = await _client.GetMetadataAsync(7);

        Assert.Equal("Add readme", metadata.Title);
        Assert.Equal("active", metadata.Status);
        Assert.Equal("contributor", metadata.AuthorLogin);
    }

    [Fact]
    public async Task GetDiffAsync_DelegatesToDiffProvider()
    {
        const string filesJson = """
            [
                { "filename": "README.md", "status": "added", "additions": 1, "deletions": 0 }
            ]
            """;

        _apiClient.GetPullRequestDetailsAsync(7).Returns(PrDetailsJson);
        _apiClient.GetPullRequestFilesAsync(7).Returns(filesJson);
        _apiClient.GetFileContentAtRefAsync("base222", "README.md").Returns((string?)null);
        _apiClient.GetFileContentAtRefAsync("head111", "README.md").Returns("# Hello");

        var diff = await _client.GetDiffAsync(7);

        Assert.NotNull(diff);
        Assert.Single(diff.Files);
        Assert.Equal("README.md", diff.Files[0].Path);
    }

    [Fact]
    public async Task GetFilesAsync_DelegatesToFilesProvider()
    {
        const string filesJson = """
            [
                { "filename": "src/App.cs", "status": "modified", "additions": 3, "deletions": 1 }
            ]
            """;

        _apiClient.GetPullRequestDetailsAsync(7).Returns(PrDetailsJson);
        _apiClient.GetPullRequestFilesAsync(7).Returns(filesJson);
        _apiClient.GetFileContentAtRefAsync("base222", "src/App.cs").Returns("old content");
        _apiClient.GetFileContentAtRefAsync("head111", "src/App.cs").Returns("new content");

        var files = await _client.GetFilesAsync(7);

        Assert.NotNull(files);
        Assert.True(files.Files.Count > 0);
    }

}
