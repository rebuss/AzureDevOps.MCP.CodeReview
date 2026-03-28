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

        var json = await _handler.ExecuteAsync(42, "/src/A.cs");

        var doc = JsonDocument.Parse(json);
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
    public async Task ExecuteAsync_ThrowsMcpException_WhenPrNumberZero()
    {
        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(0, "/src/A.cs"));

        Assert.Contains("greater than 0", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsMcpException_WhenPrNumberNegative()
    {
        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(-5, "/src/A.cs"));

        Assert.Contains("greater than 0", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsMcpException_WhenPathEmpty()
    {
        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(42, ""));

        Assert.Contains("Missing required parameter: path", ex.Message);
    }

    // --- Provider exceptions ---

    [Fact]
    public async Task ExecuteAsync_ThrowsMcpException_WhenPullRequestNotFound()
    {
        _diffProvider.GetFileDiffAsync(999, "/src/A.cs", Arg.Any<CancellationToken>())
            .ThrowsAsync(new PullRequestNotFoundException("PR #999 not found"));

        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(999, "/src/A.cs"));

        Assert.Contains("Pull Request not found", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsMcpException_WhenFileNotFoundInPullRequest()
    {
        _diffProvider.GetFileDiffAsync(42, "/src/NonExistent.cs", Arg.Any<CancellationToken>())
            .ThrowsAsync(new FileNotFoundInPullRequestException(
                "File '/src/NonExistent.cs' not found in Pull Request #42"));

        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(42, "/src/NonExistent.cs"));

        Assert.Contains("File not found in Pull Request", ex.Message);
        Assert.Contains("NonExistent.cs", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsMcpException_OnUnexpectedException()
    {
        _diffProvider.GetFileDiffAsync(42, "/src/A.cs", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Something broke"));

        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(42, "/src/A.cs"));

        Assert.Contains("Something broke", ex.Message);
    }
}