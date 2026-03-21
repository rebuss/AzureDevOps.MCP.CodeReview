using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using REBUSS.Pure.GitHub.Api;
using REBUSS.Pure.GitHub.Parsers;
using REBUSS.Pure.GitHub.Providers;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Shared;

namespace REBUSS.Pure.GitHub.Tests.Providers;

public class GitHubDiffProviderTests
{
    private readonly IGitHubApiClient _apiClient = Substitute.For<IGitHubApiClient>();
    private readonly GitHubDiffProvider _provider;

    private const string PrDetailsJson = """
        {
            "number": 42,
            "title": "Fix bug #42",
            "state": "open",
            "draft": false,
            "head": { "ref": "feature/fix-42", "sha": "aaa111" },
            "base": { "ref": "main", "sha": "bbb222" },
            "user": { "login": "dev-user" }
        }
        """;

    private const string FilesJson = """
        [
            {
                "filename": "src/File.cs",
                "status": "modified",
                "additions": 5,
                "deletions": 3
            }
        ]
        """;

    public GitHubDiffProviderTests()
    {
        _provider = new GitHubDiffProvider(
            _apiClient,
            new GitHubPullRequestParser(NullLogger<GitHubPullRequestParser>.Instance),
            new GitHubFileChangesParser(NullLogger<GitHubFileChangesParser>.Instance),
            new StructuredDiffBuilder(new LcsDiffAlgorithm(), NullLogger<StructuredDiffBuilder>.Instance),
            new FileClassifier(),
            NullLogger<GitHubDiffProvider>.Instance);
    }

    [Fact]
    public async Task GetDiffAsync_ReturnsDiff_WithCorrectMetadata()
    {
        SetupStandardMocks();
        _apiClient.GetFileContentAtRefAsync("bbb222", "src/File.cs").Returns("old line");
        _apiClient.GetFileContentAtRefAsync("aaa111", "src/File.cs").Returns("new line");

        var result = await _provider.GetDiffAsync(42);

        Assert.Equal("Fix bug #42", result.Title);
        Assert.Equal("active", result.Status);
        Assert.Equal("feature/fix-42", result.SourceBranch);
        Assert.Equal("main", result.TargetBranch);
        Assert.Single(result.Files);
        Assert.Equal("src/File.cs", result.Files[0].Path);
        Assert.Equal("edit", result.Files[0].ChangeType);
    }

    [Fact]
    public async Task GetDiffAsync_GeneratesStructuredDiff_ForModifiedFile()
    {
        SetupStandardMocks();
        _apiClient.GetFileContentAtRefAsync("bbb222", "src/File.cs").Returns("old line");
        _apiClient.GetFileContentAtRefAsync("aaa111", "src/File.cs").Returns("new line");

        var result = await _provider.GetDiffAsync(42);

        Assert.NotEmpty(result.Files[0].Hunks);
        var allLines = result.Files[0].Hunks.SelectMany(h => h.Lines).ToList();
        Assert.Contains(allLines, l => l.Op == '-' && l.Text == "old line");
        Assert.Contains(allLines, l => l.Op == '+' && l.Text == "new line");
    }

    [Fact]
    public async Task GetDiffAsync_SetsAdditionsAndDeletions()
    {
        SetupStandardMocks();
        _apiClient.GetFileContentAtRefAsync("bbb222", "src/File.cs").Returns("old line");
        _apiClient.GetFileContentAtRefAsync("aaa111", "src/File.cs").Returns("new line");

        var result = await _provider.GetDiffAsync(42);

        Assert.Equal(1, result.Files[0].Additions);
        Assert.Equal(1, result.Files[0].Deletions);
    }

    [Fact]
    public async Task GetDiffAsync_ThrowsPullRequestNotFound_On404()
    {
        _apiClient.GetPullRequestDetailsAsync(999)
            .ThrowsAsync(new HttpRequestException("Not Found", null, HttpStatusCode.NotFound));

        await Assert.ThrowsAsync<PullRequestNotFoundException>(
            () => _provider.GetDiffAsync(999));
    }

    [Fact]
    public async Task GetDiffAsync_HandlesNoFiles()
    {
        _apiClient.GetPullRequestDetailsAsync(42).Returns(PrDetailsJson);
        _apiClient.GetPullRequestFilesAsync(42).Returns("[]");

        var result = await _provider.GetDiffAsync(42);

        Assert.Empty(result.Files);
    }

    [Fact]
    public async Task GetDiffAsync_RespectsCancellation()
    {
        SetupStandardMocks();
        _apiClient.GetFileContentAtRefAsync(Arg.Any<string>(), Arg.Any<string>()).Returns("x");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _provider.GetDiffAsync(42, cts.Token));
    }

    // --- GetFileDiffAsync ---

    [Fact]
    public async Task GetFileDiffAsync_ReturnsDiff_ForMatchingFile()
    {
        SetupStandardMocks();
        _apiClient.GetFileContentAtRefAsync("bbb222", "src/File.cs").Returns("old line");
        _apiClient.GetFileContentAtRefAsync("aaa111", "src/File.cs").Returns("new line");

        var result = await _provider.GetFileDiffAsync(42, "src/File.cs");

        Assert.Equal("Fix bug #42", result.Title);
        Assert.Single(result.Files);
        Assert.Equal("src/File.cs", result.Files[0].Path);
        var allLines = result.Files[0].Hunks.SelectMany(h => h.Lines).ToList();
        Assert.Contains(allLines, l => l.Op == '-' && l.Text == "old line");
        Assert.Contains(allLines, l => l.Op == '+' && l.Text == "new line");
    }

    [Fact]
    public async Task GetFileDiffAsync_ThrowsFileNotFound_WhenPathDoesNotExist()
    {
        SetupStandardMocks();

        await Assert.ThrowsAsync<FileNotFoundInPullRequestException>(
            () => _provider.GetFileDiffAsync(42, "src/NonExistent.cs"));
    }

    [Fact]
    public async Task GetFileDiffAsync_ThrowsPullRequestNotFound_On404()
    {
        _apiClient.GetPullRequestDetailsAsync(999)
            .ThrowsAsync(new HttpRequestException("Not Found", null, HttpStatusCode.NotFound));

        await Assert.ThrowsAsync<PullRequestNotFoundException>(
            () => _provider.GetFileDiffAsync(999, "src/File.cs"));
    }

    [Fact]
    public async Task GetFileDiffAsync_IsCaseInsensitive()
    {
        SetupStandardMocks();
        _apiClient.GetFileContentAtRefAsync("bbb222", "src/File.cs").Returns("old");
        _apiClient.GetFileContentAtRefAsync("aaa111", "src/File.cs").Returns("new");

        var result = await _provider.GetFileDiffAsync(42, "SRC/FILE.CS");

        Assert.Single(result.Files);
    }

    // --- Diff skip scenarios ---

    [Theory]
    [InlineData("delete", "file deleted")]
    [InlineData("rename", "file renamed")]
    public async Task GetDiffAsync_SkipsDiff_ForChangeType(string changeType, string expectedSkipReason)
    {
        _apiClient.GetPullRequestDetailsAsync(42).Returns(PrDetailsJson);
        _apiClient.GetPullRequestFilesAsync(42).Returns($$"""
            [
                { "filename": "src/File.cs", "status": "{{changeType}}" }
            ]
            """);

        var result = await _provider.GetDiffAsync(42);

        Assert.Single(result.Files);
        Assert.Equal(expectedSkipReason, result.Files[0].SkipReason);
        Assert.Empty(result.Files[0].Hunks);
    }

    [Theory]
    [InlineData("lib/external.dll")]
    [InlineData("images/photo.jpg")]
    [InlineData("packages/archive.zip")]
    public async Task GetDiffAsync_SkipsDiff_ForBinaryFiles(string path)
    {
        _apiClient.GetPullRequestDetailsAsync(42).Returns(PrDetailsJson);
        _apiClient.GetPullRequestFilesAsync(42).Returns($$"""
            [
                { "filename": "{{path}}", "status": "modified" }
            ]
            """);

        var result = await _provider.GetDiffAsync(42);

        Assert.Equal("binary file", result.Files[0].SkipReason);
    }

    [Fact]
    public async Task GetDiffAsync_SkipsDiff_ForFullFileRewrite()
    {
        SetupStandardMocks();

        var oldContent = string.Join("\n", Enumerable.Range(1, 15).Select(i => $"old line {i}"));
        var newContent = string.Join("\n", Enumerable.Range(1, 15).Select(i => $"new line {i}"));
        _apiClient.GetFileContentAtRefAsync("bbb222", "src/File.cs").Returns(oldContent);
        _apiClient.GetFileContentAtRefAsync("aaa111", "src/File.cs").Returns(newContent);

        var result = await _provider.GetDiffAsync(42);

        Assert.Equal("full file rewrite", result.Files[0].SkipReason);
        Assert.Empty(result.Files[0].Hunks);
    }

    private void SetupStandardMocks()
    {
        _apiClient.GetPullRequestDetailsAsync(42).Returns(PrDetailsJson);
        _apiClient.GetPullRequestFilesAsync(42).Returns(FilesJson);
    }
}
