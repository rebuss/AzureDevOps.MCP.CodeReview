using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Tools;

namespace REBUSS.Pure.Tests.Tools;

public class GetPullRequestDiffToolHandlerTests
{
    private readonly IPullRequestDataProvider _diffProvider = Substitute.For<IPullRequestDataProvider>();
    private readonly GetPullRequestDiffToolHandler _handler;

    private static readonly PullRequestDiff SampleDiff = new()
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

    public GetPullRequestDiffToolHandlerTests()
    {
        _handler = new GetPullRequestDiffToolHandler(
            _diffProvider,
            NullLogger<GetPullRequestDiffToolHandler>.Instance);
    }

    // --- Happy path ---

    [Fact]
    public async Task ExecuteAsync_ReturnsStructuredJson_ByDefault()
    {
        _diffProvider.GetDiffAsync(42, Arg.Any<CancellationToken>()).Returns(SampleDiff);

        var args = CreateArgs(42);
        var result = await _handler.ExecuteAsync(args);

        Assert.False(result.IsError);
        var text = result.Content[0].Text;
        var doc = JsonDocument.Parse(text);
        Assert.Equal(42, doc.RootElement.GetProperty("prNumber").GetInt32());
        Assert.True(doc.RootElement.TryGetProperty("files", out var files));
        Assert.Equal(1, files.GetArrayLength());

        var file = files[0];
        Assert.Equal("/src/A.cs", file.GetProperty("path").GetString());
        Assert.Equal("edit", file.GetProperty("changeType").GetString());
        Assert.Equal(1, file.GetProperty("additions").GetInt32());
        Assert.Equal(1, file.GetProperty("deletions").GetInt32());

        Assert.True(file.TryGetProperty("hunks", out var hunks));
        Assert.Equal(1, hunks.GetArrayLength());
        var hunk = hunks[0];
        Assert.Equal(1, hunk.GetProperty("oldStart").GetInt32());
        Assert.True(hunk.TryGetProperty("lines", out var lines));
        Assert.Equal(2, lines.GetArrayLength());
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotIncludeMetadataInOutput()
    {
        _diffProvider.GetDiffAsync(42, Arg.Any<CancellationToken>()).Returns(SampleDiff);

        var args = CreateArgs(42);
        var result = await _handler.ExecuteAsync(args);

        Assert.False(result.IsError);
        var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.False(doc.RootElement.TryGetProperty("title", out _));
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
        var result = await _handler.ExecuteAsync(new Dictionary<string, object>());

        Assert.True(result.IsError);
        Assert.Contains("Missing required parameter", result.Content[0].Text);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenPrNumberNotInteger()
    {
        var args = new Dictionary<string, object> { ["prNumber"] = "not-a-number" };
        var result = await _handler.ExecuteAsync(args);

        Assert.True(result.IsError);
        Assert.Contains("must be an integer", result.Content[0].Text);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenPrNumberZero()
    {
        var args = CreateArgs(0);
        var result = await _handler.ExecuteAsync(args);

        Assert.True(result.IsError);
        Assert.Contains("greater than 0", result.Content[0].Text);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenPrNumberNegative()
    {
        var args = CreateArgs(-5);
        var result = await _handler.ExecuteAsync(args);

        Assert.True(result.IsError);
        Assert.Contains("greater than 0", result.Content[0].Text);
    }

    // --- JsonElement input (real MCP scenario) ---

    [Fact]
    public async Task ExecuteAsync_HandlesPrNumberAsJsonElement()
    {
        _diffProvider.GetDiffAsync(42, Arg.Any<CancellationToken>()).Returns(SampleDiff);

        // Simulate what happens when MCP JSON-RPC deserializes arguments
        var json = JsonSerializer.Deserialize<Dictionary<string, object>>(
            """{"prNumber": 42}""")!;
        var result = await _handler.ExecuteAsync(json);

        Assert.False(result.IsError);
    }

    // --- Provider exceptions ---

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenPullRequestNotFound()
    {
        _diffProvider.GetDiffAsync(999, Arg.Any<CancellationToken>())
            .ThrowsAsync(new PullRequestNotFoundException("PR #999 not found"));

        var result = await _handler.ExecuteAsync(CreateArgs(999));

        Assert.True(result.IsError);
        Assert.Contains("not found", result.Content[0].Text);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_OnUnexpectedException()
    {
        _diffProvider.GetDiffAsync(42, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Something broke"));

        var result = await _handler.ExecuteAsync(CreateArgs(42));

        Assert.True(result.IsError);
        Assert.Contains("Something broke", result.Content[0].Text);
    }

    // --- Helpers ---

    private static Dictionary<string, object> CreateArgs(int prNumber)
    {
        return new Dictionary<string, object> { ["prNumber"] = prNumber };
    }
}
