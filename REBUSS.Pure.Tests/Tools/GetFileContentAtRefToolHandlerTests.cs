using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
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

        var args = CreateArgs("src/Cache/CacheService.cs", "abc123def456");
        var result = await _handler.ExecuteAsync(args);

        Assert.False(result.IsError);
        var doc = JsonDocument.Parse(result.Content[0].Text);
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

        var args = CreateArgs("image.png", "abc123");
        var result = await _handler.ExecuteAsync(args);

        Assert.False(result.IsError);
        var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.True(doc.RootElement.GetProperty("isBinary").GetBoolean());
        Assert.Equal("base64", doc.RootElement.GetProperty("encoding").GetString());
    }

    // --- Validation errors ---

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenArgumentsNull()
    {
        var result = await _handler.ExecuteAsync(null);

        Assert.True(result.IsError);
        Assert.Contains("Missing required parameter: path", result.Content[0].Text);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenPathMissing()
    {
        var args = new Dictionary<string, object> { ["ref"] = "abc123" };
        var result = await _handler.ExecuteAsync(args);

        Assert.True(result.IsError);
        Assert.Contains("Missing required parameter: path", result.Content[0].Text);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenRefMissing()
    {
        var args = new Dictionary<string, object> { ["path"] = "src/File.cs" };
        var result = await _handler.ExecuteAsync(args);

        Assert.True(result.IsError);
        Assert.Contains("Missing required parameter: ref", result.Content[0].Text);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenPathEmpty()
    {
        var args = CreateArgs("", "abc123");
        var result = await _handler.ExecuteAsync(args);

        Assert.True(result.IsError);
        Assert.Contains("path parameter must not be empty", result.Content[0].Text);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenRefEmpty()
    {
        var args = CreateArgs("src/File.cs", "");
        var result = await _handler.ExecuteAsync(args);

        Assert.True(result.IsError);
        Assert.Contains("ref parameter must not be empty", result.Content[0].Text);
    }

    // --- JsonElement input (real MCP scenario) ---

    [Fact]
    public async Task ExecuteAsync_HandlesPathAndRefAsJsonElement()
    {
        _fileContentProvider.GetFileContentAsync("src/File.cs", "abc123", Arg.Any<CancellationToken>())
            .Returns(SampleFileContent);

        var json = JsonSerializer.Deserialize<Dictionary<string, object>>(
            """{"path": "src/File.cs", "ref": "abc123"}""")!;
        var result = await _handler.ExecuteAsync(json);

        Assert.False(result.IsError);
    }

    // --- Provider exceptions ---

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenFileContentNotFound()
    {
        _fileContentProvider.GetFileContentAsync("src/Missing.cs", "abc123", Arg.Any<CancellationToken>())
            .ThrowsAsync(new FileContentNotFoundException("File 'src/Missing.cs' not found at ref 'abc123'"));

        var result = await _handler.ExecuteAsync(CreateArgs("src/Missing.cs", "abc123"));

        Assert.True(result.IsError);
        Assert.Contains("File not found", result.Content[0].Text);
        Assert.Contains("Missing.cs", result.Content[0].Text);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenRefInvalid()
    {
        _fileContentProvider.GetFileContentAsync("src/File.cs", "invalid-ref", Arg.Any<CancellationToken>())
            .ThrowsAsync(new FileContentNotFoundException("File 'src/File.cs' not found at ref 'invalid-ref'"));

        var result = await _handler.ExecuteAsync(CreateArgs("src/File.cs", "invalid-ref"));

        Assert.True(result.IsError);
        Assert.Contains("File not found", result.Content[0].Text);
        Assert.Contains("invalid-ref", result.Content[0].Text);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_OnUnexpectedException()
    {
        _fileContentProvider.GetFileContentAsync("src/File.cs", "abc123", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Something broke"));

        var result = await _handler.ExecuteAsync(CreateArgs("src/File.cs", "abc123"));

        Assert.True(result.IsError);
        Assert.Contains("Something broke", result.Content[0].Text);
    }

    // --- Tool definition ---

    [Fact]
    public void ToolName_IsGetFileContentAtRef()
    {
        Assert.Equal("get_file_content_at_ref", _handler.ToolName);
    }

    [Fact]
    public void GetToolDefinition_HasCorrectSchema()
    {
        var tool = _handler.GetToolDefinition();

        Assert.Equal("get_file_content_at_ref", tool.Name);
        Assert.Contains("path", tool.InputSchema.Properties.Keys);
        Assert.Equal("string", tool.InputSchema.Properties["path"].Type);
        Assert.Contains("ref", tool.InputSchema.Properties.Keys);
        Assert.Equal("string", tool.InputSchema.Properties["ref"].Type);
        Assert.Contains("path", tool.InputSchema.Required!);
        Assert.Contains("ref", tool.InputSchema.Required!);
    }

    // --- Helpers ---

    private static Dictionary<string, object> CreateArgs(string path, string gitRef)
    {
        return new Dictionary<string, object>
        {
            ["path"] = path,
            ["ref"] = gitRef
        };
    }
}
