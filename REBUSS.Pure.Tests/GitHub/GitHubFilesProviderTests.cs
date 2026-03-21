using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using REBUSS.Pure.GitHub.Api;
using REBUSS.Pure.GitHub.Parsers;
using REBUSS.Pure.GitHub.Providers;
using REBUSS.Pure.Core.Shared;

namespace REBUSS.Pure.Tests.GitHub;

public class GitHubFilesProviderTests
{
    private readonly IGitHubApiClient _apiClient = Substitute.For<IGitHubApiClient>();
    private readonly GitHubFilesProvider _provider;

    private const string PrDetailsJson = """
        {
            "number": 10,
            "title": "Feature",
            "state": "open",
            "head": { "ref": "feature", "sha": "head111" },
            "base": { "ref": "main", "sha": "base222" },
            "user": { "login": "dev" }
        }
        """;

    public GitHubFilesProviderTests()
    {
        var prParser = new GitHubPullRequestParser(NullLogger<GitHubPullRequestParser>.Instance);
        var changesParser = new GitHubFileChangesParser(NullLogger<GitHubFileChangesParser>.Instance);
        var diffBuilder = new StructuredDiffBuilder(new LcsDiffAlgorithm(), NullLogger<StructuredDiffBuilder>.Instance);
        var fileClassifier = new FileClassifier();

        var diffProvider = new GitHubDiffProvider(
            _apiClient, prParser, changesParser, diffBuilder, fileClassifier,
            NullLogger<GitHubDiffProvider>.Instance);

        _provider = new GitHubFilesProvider(
            diffProvider, fileClassifier,
            NullLogger<GitHubFilesProvider>.Instance);
    }

    [Fact]
    public async Task GetFilesAsync_ReturnsClassifiedFiles()
    {
        const string filesJson = """
            [
                { "filename": "src/App.cs", "status": "modified" },
                { "filename": "tests/AppTests.cs", "status": "added" }
            ]
            """;

        _apiClient.GetPullRequestDetailsAsync(10).Returns(PrDetailsJson);
        _apiClient.GetPullRequestFilesAsync(10).Returns(filesJson);
        _apiClient.GetFileContentAtRefAsync("base222", "src/App.cs").Returns("old");
        _apiClient.GetFileContentAtRefAsync("head111", "src/App.cs").Returns("new");
        _apiClient.GetFileContentAtRefAsync(Arg.Any<string>(), "tests/AppTests.cs").Returns((string?)null);
        _apiClient.GetFileContentAtRefAsync("head111", "tests/AppTests.cs").Returns("test code");

        var result = await _provider.GetFilesAsync(10);

        Assert.NotNull(result);
        Assert.True(result.Files.Count >= 1);
    }

    [Fact]
    public async Task GetFilesAsync_IncludesSummary()
    {
        const string filesJson = """
            [
                { "filename": "src/Service.cs", "status": "modified" }
            ]
            """;

        _apiClient.GetPullRequestDetailsAsync(10).Returns(PrDetailsJson);
        _apiClient.GetPullRequestFilesAsync(10).Returns(filesJson);
        _apiClient.GetFileContentAtRefAsync("base222", "src/Service.cs").Returns("old code");
        _apiClient.GetFileContentAtRefAsync("head111", "src/Service.cs").Returns("new code");

        var result = await _provider.GetFilesAsync(10);

        Assert.NotNull(result.Summary);
        Assert.True(result.Summary.SourceFiles > 0);
    }

    [Fact]
    public async Task GetFilesAsync_EmptyFilesList_ReturnsEmptyResult()
    {
        _apiClient.GetPullRequestDetailsAsync(10).Returns(PrDetailsJson);
        _apiClient.GetPullRequestFilesAsync(10).Returns("[]");

        var result = await _provider.GetFilesAsync(10);

        Assert.Empty(result.Files);
    }
}
