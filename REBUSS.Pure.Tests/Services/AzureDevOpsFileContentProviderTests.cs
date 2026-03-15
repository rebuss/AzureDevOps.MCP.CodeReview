using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using REBUSS.Pure.AzureDevOpsIntegration.Services;
using REBUSS.Pure.Services.Common;
using REBUSS.Pure.Services.Content;

namespace REBUSS.Pure.Tests.Services;

public class AzureDevOpsFileContentProviderTests
{
    private readonly IAzureDevOpsApiClient _apiClient = Substitute.For<IAzureDevOpsApiClient>();
    private readonly AzureDevOpsFileContentProvider _provider;

    public AzureDevOpsFileContentProviderTests()
    {
        _provider = new AzureDevOpsFileContentProvider(
            _apiClient,
            NullLogger<AzureDevOpsFileContentProvider>.Instance);
    }

    // --- Happy path: text file ---

    [Fact]
    public async Task GetFileContentAsync_ReturnsTextContent_ForCommitSha()
    {
        _apiClient.GetFileContentAtRefAsync("abc123def456", "/src/Cache/CacheService.cs")
            .Returns("public class CacheService { }");

        var result = await _provider.GetFileContentAsync("/src/Cache/CacheService.cs", "abc123def456");

        Assert.Equal("src/Cache/CacheService.cs", result.Path);
        Assert.Equal("abc123def456", result.Ref);
        Assert.Equal("utf-8", result.Encoding);
        Assert.False(result.IsBinary);
        Assert.Equal("public class CacheService { }", result.Content);
        Assert.True(result.Size > 0);
    }

    [Fact]
    public async Task GetFileContentAsync_ReturnsTextContent_ForBranchName()
    {
        _apiClient.GetFileContentAtRefAsync("main", "/src/File.cs")
            .Returns("content at main");

        var result = await _provider.GetFileContentAsync("/src/File.cs", "main");

        Assert.Equal("src/File.cs", result.Path);
        Assert.Equal("main", result.Ref);
        Assert.Equal("utf-8", result.Encoding);
        Assert.Equal("content at main", result.Content);
    }

    [Fact]
    public async Task GetFileContentAsync_ReturnsTextContent_ForTagRef()
    {
        _apiClient.GetFileContentAtRefAsync("refs/tags/v1.0", "/src/File.cs")
            .Returns("content at tag");

        var result = await _provider.GetFileContentAsync("/src/File.cs", "refs/tags/v1.0");

        Assert.Equal("refs/tags/v1.0", result.Ref);
        Assert.Equal("content at tag", result.Content);
    }

    [Fact]
    public async Task GetFileContentAsync_TrimsLeadingSlashFromPath()
    {
        _apiClient.GetFileContentAtRefAsync("abc123", "/src/File.cs")
            .Returns("content");

        var result = await _provider.GetFileContentAsync("/src/File.cs", "abc123");

        Assert.Equal("src/File.cs", result.Path);
    }

    [Fact]
    public async Task GetFileContentAsync_ComputesSizeInBytes()
    {
        var content = "Hello, World!"; // 13 bytes in UTF-8
        _apiClient.GetFileContentAtRefAsync("abc123", "/file.txt")
            .Returns(content);

        var result = await _provider.GetFileContentAsync("/file.txt", "abc123");

        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(content), result.Size);
    }

    // --- Binary content ---

    [Fact]
    public async Task GetFileContentAsync_DetectsBinaryContent_WithNullCharacters()
    {
        var binaryContent = "some\0binary\0content";
        _apiClient.GetFileContentAtRefAsync("abc123", "/image.png")
            .Returns(binaryContent);

        var result = await _provider.GetFileContentAsync("/image.png", "abc123");

        Assert.True(result.IsBinary);
        Assert.Equal("base64", result.Encoding);
        Assert.Equal(
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(binaryContent)),
            result.Content);
    }

    // --- Error cases ---

    [Fact]
    public async Task GetFileContentAsync_ThrowsFileContentNotFoundException_WhenFileNotFound()
    {
        _apiClient.GetFileContentAtRefAsync("abc123", "/nonexistent.cs")
            .Returns((string?)null);

        var ex = await Assert.ThrowsAsync<FileContentNotFoundException>(
            () => _provider.GetFileContentAsync("/nonexistent.cs", "abc123"));

        Assert.Contains("nonexistent.cs", ex.Message);
        Assert.Contains("abc123", ex.Message);
    }

    [Fact]
    public async Task GetFileContentAsync_ThrowsFileContentNotFoundException_WhenRefInvalid()
    {
        _apiClient.GetFileContentAtRefAsync("invalid-ref", "/src/File.cs")
            .Returns((string?)null);

        var ex = await Assert.ThrowsAsync<FileContentNotFoundException>(
            () => _provider.GetFileContentAsync("/src/File.cs", "invalid-ref"));

        Assert.Contains("invalid-ref", ex.Message);
    }

    [Fact]
    public async Task GetFileContentAsync_RespectsCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _provider.GetFileContentAsync("/src/File.cs", "abc123", cts.Token));
    }

}
