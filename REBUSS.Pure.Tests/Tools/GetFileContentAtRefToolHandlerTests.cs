using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
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

    private static string AllText(IEnumerable<ContentBlock> blocks) =>
        string.Join("\n", blocks.Cast<TextContentBlock>().Select(b => b.Text));

    [Fact]
    public async Task ExecuteAsync_ReturnsPlainText_WithCorrectContent()
    {
        _fileContentProvider.GetFileContentAsync("src/Cache/CacheService.cs", "abc123def456", Arg.Any<CancellationToken>())
            .Returns(SampleFileContent);

        var blocks = (await _handler.ExecuteAsync("src/Cache/CacheService.cs", "abc123def456")).ToList();
        var text = AllText(blocks);

        Assert.Contains("src/Cache/CacheService.cs", text);
        Assert.Contains("abc123def456", text);
        Assert.Contains("public class CacheService { }", text);
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

        var blocks = (await _handler.ExecuteAsync("image.png", "abc123")).ToList();
        var text = AllText(blocks);

        Assert.Contains("[binary file", text);
    }

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

    [Fact]
    public async Task ExecuteAsync_ThrowsMcpException_WhenFileContentNotFound()
    {
        _fileContentProvider.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new FileContentNotFoundException("not found"));

        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync("src/File.cs", "abc123"));
        Assert.Contains("not found", ex.Message.ToLower());
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsMcpException_OnUnexpectedException()
    {
        _fileContentProvider.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Something broke"));

        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync("src/File.cs", "abc123"));
        Assert.Contains("Something broke", ex.Message);
    }
}
