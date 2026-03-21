using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using REBUSS.Pure.GitHub.Api;
using REBUSS.Pure.GitHub.Providers;
using REBUSS.Pure.Core.Exceptions;

namespace REBUSS.Pure.GitHub.Tests.Providers;

public class GitHubFileContentProviderTests
{
    private readonly IGitHubApiClient _apiClient = Substitute.For<IGitHubApiClient>();
    private readonly GitHubFileContentProvider _provider;

    public GitHubFileContentProviderTests()
    {
        _provider = new GitHubFileContentProvider(
            _apiClient,
            NullLogger<GitHubFileContentProvider>.Instance);
    }

    [Fact]
    public async Task GetFileContentAsync_ReturnsContent()
    {
        _apiClient.GetFileContentAtRefAsync("main", "src/App.cs")
            .Returns("namespace App;");

        var result = await _provider.GetFileContentAsync("src/App.cs", "main");

        Assert.Equal("namespace App;", result.Content);
        Assert.Equal("src/App.cs", result.Path);
        Assert.Equal("main", result.Ref);
        Assert.False(result.IsBinary);
        Assert.Equal("utf-8", result.Encoding);
    }

    [Fact]
    public async Task GetFileContentAsync_TrimsLeadingSlash()
    {
        _apiClient.GetFileContentAtRefAsync("abc123", "/src/File.cs")
            .Returns("content");

        var result = await _provider.GetFileContentAsync("/src/File.cs", "abc123");

        Assert.Equal("src/File.cs", result.Path);
    }

    [Fact]
    public async Task GetFileContentAsync_DetectsBinaryContent()
    {
        _apiClient.GetFileContentAtRefAsync("main", "image.png")
            .Returns("binary\0content");

        var result = await _provider.GetFileContentAsync("image.png", "main");

        Assert.True(result.IsBinary);
        Assert.Equal("base64", result.Encoding);
    }

    [Fact]
    public async Task GetFileContentAsync_Throws_WhenFileNotFound()
    {
        _apiClient.GetFileContentAtRefAsync("main", "missing.cs")
            .Returns((string?)null);

        await Assert.ThrowsAsync<FileContentNotFoundException>(
            () => _provider.GetFileContentAsync("missing.cs", "main"));
    }

    [Fact]
    public async Task GetFileContentAsync_CalculatesSize()
    {
        _apiClient.GetFileContentAtRefAsync("main", "file.txt")
            .Returns("hello world");

        var result = await _provider.GetFileContentAsync("file.txt", "main");

        Assert.True(result.Size > 0);
    }

    [Fact]
    public async Task GetFileContentAsync_ThrowsOnCancellation()
    {
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _provider.GetFileContentAsync("file.cs", "main", cts.Token));
    }
}
