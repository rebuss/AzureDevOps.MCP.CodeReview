using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Tools;

namespace REBUSS.Pure.Tests.Tools;

public class GetFileDiffToolHandlerTests
{
    private readonly IPullRequestDataProvider _diffProvider = Substitute.For<IPullRequestDataProvider>();
    private readonly GetFileDiffToolHandler _handler;

    private static readonly PullRequestDiff SampleFileDiff = new()
    {
        Title = "Fix bug",
        Status = "active",
        SourceBranch = "feature/x",
        TargetBranch = "main",
        SourceRefName = "refs/heads/feature/x",
        TargetRefName = "refs/heads/main",
        Files = new List<FileChange>
        {
            new()
            {
                Path = "/src/A.cs",
                ChangeType = "edit",
                Additions = 1,
                Deletions = 1,
                Hunks = new List<DiffHunk>
                {
                    new()
                    {
                        OldStart = 1, OldCount = 1, NewStart = 1, NewCount = 1,
                        Lines = new List<DiffLine>
                        {
                            new() { Op = '-', Text = "old" },
                            new() { Op = '+', Text = "new" }
                        }
                    }
                }
            }
        }
    };

    public GetFileDiffToolHandlerTests()
    {
        _handler = new GetFileDiffToolHandler(
            _diffProvider,
            NullLogger<GetFileDiffToolHandler>.Instance);
    }

    // --- Happy path ---

    [Fact]
    public async Task ExecuteAsync_ReturnsStructuredJson_ByDefault()
    {
        _diffProvider.GetFileDiffAsync(42, "/src/A.cs", Arg.Any<CancellationToken>())
            .Returns(SampleFileDiff);

        var args = CreateArgs(42, "/src/A.cs");
        var result = await _handler.ExecuteAsync(args);

        Assert.False(result.IsError);
        var text = result.Content[0].Text;
        var doc = JsonDocument.Parse(text);
        Assert.Equal(42, doc.RootElement.GetProperty("prNumber").GetInt32());
        Assert.True(doc.RootElement.TryGetProperty("files", out var files));
        Assert.Equal(1, files.GetArrayLength());
        Assert.Equal("/src/A.cs", files[0].GetProperty("path").GetString());

        var hunks = files[0].GetProperty("hunks");
        Assert.Equal(1, hunks.GetArrayLength());
        Assert.True(hunks[0].TryGetProperty("lines", out var lines));
        Assert.Equal(2, lines.GetArrayLength());
    }

    // --- Validation errors ---

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenArgumentsNull()
    {
        var result = await _handler.ExecuteAsync(null);

        Assert.True(result.IsError);
        Assert.Contains("Missing required parameter", result.Content[0].Text);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenPrNumberMissing()
    {
        var args = new Dictionary<string, object> { ["path"] = "/src/A.cs" };
        var result = await _handler.ExecuteAsync(args);

        Assert.True(result.IsError);
        Assert.Contains("Missing required parameter: prNumber", result.Content[0].Text);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenPathMissing()
    {
        var args = new Dictionary<string, object> { ["prNumber"] = 42 };
        var result = await _handler.ExecuteAsync(args);

        Assert.True(result.IsError);
        Assert.Contains("Missing required parameter: path", result.Content[0].Text);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenPathEmpty()
    {
        var args = CreateArgs(42, "");
        var result = await _handler.ExecuteAsync(args);

        Assert.True(result.IsError);
        Assert.Contains("path parameter must not be empty", result.Content[0].Text);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenPrNumberNotInteger()
    {
        var args = new Dictionary<string, object>
        {
            ["prNumber"] = "not-a-number",
            ["path"] = "/src/A.cs"
        };
        var result = await _handler.ExecuteAsync(args);

        Assert.True(result.IsError);
        Assert.Contains("must be an integer", result.Content[0].Text);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenPrNumberZero()
    {
        var args = CreateArgs(0, "/src/A.cs");
        var result = await _handler.ExecuteAsync(args);

        Assert.True(result.IsError);
        Assert.Contains("greater than 0", result.Content[0].Text);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenPrNumberNegative()
    {
        var args = CreateArgs(-5, "/src/A.cs");
        var result = await _handler.ExecuteAsync(args);

        Assert.True(result.IsError);
        Assert.Contains("greater than 0", result.Content[0].Text);
    }

    // --- JsonElement input (real MCP scenario) ---

    [Fact]
    public async Task ExecuteAsync_HandlesPrNumberAndPathAsJsonElement()
    {
        _diffProvider.GetFileDiffAsync(42, "/src/A.cs", Arg.Any<CancellationToken>())
            .Returns(SampleFileDiff);

        var json = JsonSerializer.Deserialize<Dictionary<string, object>>(
            """{"prNumber": 42, "path": "/src/A.cs"}""")!;
        var result = await _handler.ExecuteAsync(json);

        Assert.False(result.IsError);
    }

    // --- Provider exceptions ---

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenPullRequestNotFound()
    {
        _diffProvider.GetFileDiffAsync(999, "/src/A.cs", Arg.Any<CancellationToken>())
            .ThrowsAsync(new PullRequestNotFoundException("PR #999 not found"));

        var result = await _handler.ExecuteAsync(CreateArgs(999, "/src/A.cs"));

        Assert.True(result.IsError);
        Assert.Contains("Pull Request not found", result.Content[0].Text);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenFileNotFoundInPullRequest()
    {
        _diffProvider.GetFileDiffAsync(42, "/src/NonExistent.cs", Arg.Any<CancellationToken>())
            .ThrowsAsync(new FileNotFoundInPullRequestException(
                "File '/src/NonExistent.cs' not found in Pull Request #42"));

        var result = await _handler.ExecuteAsync(CreateArgs(42, "/src/NonExistent.cs"));

        Assert.True(result.IsError);
        Assert.Contains("File not found in Pull Request", result.Content[0].Text);
        Assert.Contains("NonExistent.cs", result.Content[0].Text);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_OnUnexpectedException()
    {
        _diffProvider.GetFileDiffAsync(42, "/src/A.cs", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Something broke"));

        var result = await _handler.ExecuteAsync(CreateArgs(42, "/src/A.cs"));

        Assert.True(result.IsError);
        Assert.Contains("Something broke", result.Content[0].Text);
    }

    // --- Tool definition ---

    [Fact]
    public void ToolName_IsGetFileDiff()
    {
        Assert.Equal("get_file_diff", _handler.ToolName);
    }

    [Fact]
    public void GetToolDefinition_HasCorrectSchema()
    {
        var tool = _handler.GetToolDefinition();

        Assert.Equal("get_file_diff", tool.Name);
        Assert.Contains("prNumber", tool.InputSchema.Properties.Keys);
        Assert.Equal("integer", tool.InputSchema.Properties["prNumber"].Type);
        Assert.Contains("path", tool.InputSchema.Properties.Keys);
        Assert.Equal("string", tool.InputSchema.Properties["path"].Type);
        Assert.DoesNotContain("format", tool.InputSchema.Properties.Keys);
        Assert.Contains("prNumber", tool.InputSchema.Required!);
        Assert.Contains("path", tool.InputSchema.Required!);
    }

    // --- Helpers ---

    private static Dictionary<string, object> CreateArgs(int prNumber, string path)
    {
        return new Dictionary<string, object>
        {
            ["prNumber"] = prNumber,
            ["path"] = path
        };
    }
}
