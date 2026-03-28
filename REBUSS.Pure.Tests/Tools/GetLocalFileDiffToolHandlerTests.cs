using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Services.LocalReview;
using REBUSS.Pure.Services.ResponsePacking;
using REBUSS.Pure.Tools;

namespace REBUSS.Pure.Tests.Tools;

public class GetLocalFileDiffToolHandlerTests
{
    private readonly ILocalReviewProvider _reviewProvider = Substitute.For<ILocalReviewProvider>();
    private readonly IContextBudgetResolver _budgetResolver = Substitute.For<IContextBudgetResolver>();
    private readonly ITokenEstimator _tokenEstimator = Substitute.For<ITokenEstimator>();
    private readonly IFileClassifier _fileClassifier = Substitute.For<IFileClassifier>();
    private readonly GetLocalFileDiffToolHandler _handler;

    private static readonly PullRequestDiff SampleDiff = new()
    {
        Title = "Local changes (working-tree)",
        Status = "local",
        Files = new List<FileChange>
        {
            new()
            {
                Path = "src/Service.cs",
                ChangeType = "edit",
                Additions = 1,
                Deletions = 1,
                Hunks = new List<DiffHunk>
                {
                    new()
                    {
                        OldStart = 5, OldCount = 1, NewStart = 5, NewCount = 1,
                        Lines = new List<DiffLine>
                        {
                            new() { Op = '-', Text = "old code" },
                            new() { Op = '+', Text = "new code" }
                        }
                    }
                }
            }
        }
    };

    public GetLocalFileDiffToolHandlerTests()
    {
        _budgetResolver.Resolve(Arg.Any<int?>(), Arg.Any<string?>())
            .Returns(new BudgetResolutionResult(200000, 140000, BudgetSource.Default, Array.Empty<string>()));
        _tokenEstimator.Estimate(Arg.Any<string>(), Arg.Any<int>())
            .Returns(new TokenEstimationResult(100, 0.07, true));
        _fileClassifier.Classify(Arg.Any<string>())
            .Returns(new FileClassification { Category = FileCategory.Source, Extension = ".cs", IsBinary = false, IsGenerated = false, IsTestFile = false, ReviewPriority = "high" });

        _handler = new GetLocalFileDiffToolHandler(
            _reviewProvider,
            new ResponsePacker(NullLogger<ResponsePacker>.Instance),
            _budgetResolver,
            _tokenEstimator,
            _fileClassifier,
            NullLogger<GetLocalFileDiffToolHandler>.Instance);
    }

    // --- Happy path ---

    [Fact]
    public async Task ExecuteAsync_ReturnsStructuredJson()
    {
        _reviewProvider.GetFileDiffAsync(
                "src/Service.cs", Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(SampleDiff);

        var json = await _handler.ExecuteAsync("src/Service.cs");

        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("files", out var files));
        Assert.Equal(1, files.GetArrayLength());

        var file = files[0];
        Assert.Equal("src/Service.cs", file.GetProperty("path").GetString());
        Assert.Equal("edit", file.GetProperty("changeType").GetString());
        Assert.Equal(1, file.GetProperty("additions").GetInt32());

        // prNumber should be absent from local diff output
        Assert.False(doc.RootElement.TryGetProperty("prNumber", out _));

        var hunks = file.GetProperty("hunks");
        Assert.Equal(1, hunks.GetArrayLength());
        var lines = hunks[0].GetProperty("lines");
        Assert.Equal(2, lines.GetArrayLength());
    }

    [Fact]
    public async Task ExecuteAsync_DefaultsToWorkingTree_WhenNoScope()
    {
        _reviewProvider.GetFileDiffAsync(
                Arg.Any<string>(), Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(SampleDiff);

        await _handler.ExecuteAsync("src/Service.cs");

        await _reviewProvider.Received(1).GetFileDiffAsync(
            "src/Service.cs",
            Arg.Is<LocalReviewScope>(s => s.Kind == LocalReviewScopeKind.WorkingTree),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ParsesStagedScope()
    {
        _reviewProvider.GetFileDiffAsync(
                Arg.Any<string>(), Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(SampleDiff);

        await _handler.ExecuteAsync("src/Service.cs", scope: "staged");

        await _reviewProvider.Received(1).GetFileDiffAsync(
            "src/Service.cs",
            Arg.Is<LocalReviewScope>(s => s.Kind == LocalReviewScopeKind.Staged),
            Arg.Any<CancellationToken>());
    }

    // --- Validation errors ---

    [Fact]
    public async Task ExecuteAsync_ThrowsMcpException_WhenPathEmpty()
    {
        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(""));

        Assert.Contains("Missing required parameter: path", ex.Message);
    }

    // --- Provider exceptions ---

    [Fact]
    public async Task ExecuteAsync_ThrowsMcpException_WhenRepositoryNotFound()
    {
        _reviewProvider.GetFileDiffAsync(
                Arg.Any<string>(), Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new LocalRepositoryNotFoundException("No repo"));

        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync("src/Service.cs"));

        Assert.Contains("Repository not found", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsMcpException_WhenFileNotFoundInLocalChanges()
    {
        _reviewProvider.GetFileDiffAsync(
                Arg.Any<string>(), Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new LocalFileNotFoundException("File not found"));

        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync("src/NotChanged.cs"));

        Assert.Contains("File not found in local changes", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsMcpException_OnUnexpectedException()
    {
        _reviewProvider.GetFileDiffAsync(
                Arg.Any<string>(), Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync("src/Service.cs"));

        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsMcpException_WhenGitCommandFails()
    {
        _reviewProvider.GetFileDiffAsync(
                Arg.Any<string>(), Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new GitCommandException(128, "fatal: bad revision"));

        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync("src/Service.cs"));

        Assert.Contains("Git command failed", ex.Message);
    }

    // --- Packing integration ---

    [Fact]
    public async Task ExecuteAsync_IncludesManifest_InResponse()
    {
        _reviewProvider.GetFileDiffAsync(
                "src/Service.cs", Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(SampleDiff);

        var json = await _handler.ExecuteAsync("src/Service.cs");

        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("manifest", out var manifest));
        Assert.True(manifest.TryGetProperty("summary", out var summary));
        Assert.Equal(1, summary.GetProperty("totalItems").GetInt32());
    }
}