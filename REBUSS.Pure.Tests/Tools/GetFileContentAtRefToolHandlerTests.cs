using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Tools;

namespace REBUSS.Pure.Tests.Tools;

public class GetFileContentAtRefToolHandlerTests
{
    private readonly IFileContentDataProvider _fileContentProvider = Substitute.For<IFileContentDataProvider>();
    private readonly GetFileContentAtRefToolHandler _handler;

    private static readonly FileContent SampleFileContent = new()
    {
        Path = "src/Cache/CacheService.cs",
        Ref = "abc123def456",
        Size = 30,
        Encoding = "utf-8",
        Content = "public class CacheService { }",
        IsBinary = false
    };

    public GetFileContentAtRefToolHandlerTests()
    {
        _handler = new GetFileContentAtRefToolHandler(
            _fileContentProvider,
            NullLogger<GetFileContentAtRefToolHandler>.Instance);
    }

    // --- Happy path ---

    [Fact]
    public async Task ExecuteAsync_ReturnsStructuredJson_WithCorrectFields()
    {
        _fileContentProvider.GetFileContentAsync("src/Cache/CacheService.cs", "abc123def456", Arg.Any<CancellationToken>())
            .Returns(SampleFileContent);

        var json = await _handler.ExecuteAsync("src/Cache/CacheService.cs", "abc123def456");

        var doc = JsonDocument.Parse(json);
        Assert.Equal("src/Cache/CacheService.cs", doc.RootElement.GetProperty("path").GetString());
        Assert.Equal("abc123def456", doc.RootElement.GetProperty("ref").GetString());
        Assert.Equal(30, doc.RootElement.GetProperty("size").GetInt32());
        Assert.Equal("utf-8", doc.RootElement.GetProperty("encoding").GetString());
        Assert.Equal("public class CacheService { }", doc.RootElement.GetProperty("content").GetString());
        Assert.False(doc.RootElement.GetProperty("isBinary").GetBoolean());
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsBinaryResult_WhenContentIsBinary()
    {
        var binaryContent = new FileContent
        {
            Path = "image.png",
            Ref = "abc123",
            Size = 100,
            Encoding = "base64",
            Content = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47 }),
            IsBinary = true
        };

        _fileContentProvider.GetFileContentAsync("image.png", "abc123", Arg.Any<CancellationToken>())
            .Returns(binaryContent);

        var json = await _handler.ExecuteAsync("image.png", "abc123");

        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("isBinary").GetBoolean());
        Assert.Equal("base64", doc.RootElement.GetProperty("encoding").GetString());
    }

    // --- Validation errors ---

    [Fact]
    public async Task ExecuteAsync_ThrowsMcpException_WhenPathEmpty()
    {
        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync("", "abc123"));

        Assert.Contains("Missing required parameter: path", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsMcpException_WhenRefEmpty()
    {
        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync("src/File.cs", ""));

        Assert.Contains("Missing required parameter: ref", ex.Message);
    }

    // --- Provider exceptions ---

    [Fact]
    public async Task ExecuteAsync_ThrowsMcpException_WhenFileContentNotFound()
    {
        _fileContentProvider.GetFileContentAsync("src/Missing.cs", "abc123", Arg.Any<CancellationToken>())
            .ThrowsAsync(new FileContentNotFoundException("File 'src/Missing.cs' not found at ref 'abc123'"));

        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync("src/Missing.cs", "abc123"));

        Assert.Contains("File not found", ex.Message);
        Assert.Contains("Missing.cs", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsMcpException_WhenRefInvalid()
    {
        _fileContentProvider.GetFileContentAsync("src/File.cs", "invalid-ref", Arg.Any<CancellationToken>())
            .ThrowsAsync(new FileContentNotFoundException("File 'src/File.cs' not found at ref 'invalid-ref'"));

        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync("src/File.cs", "invalid-ref"));

        Assert.Contains("File not found", ex.Message);
        Assert.Contains("invalid-ref", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsMcpException_OnUnexpectedException()
    {
        _fileContentProvider.GetFileContentAsync("src/File.cs", "abc123", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Something broke"));

        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync("src/File.cs", "abc123"));

        Assert.Contains("Something broke", ex.Message);
    }
}