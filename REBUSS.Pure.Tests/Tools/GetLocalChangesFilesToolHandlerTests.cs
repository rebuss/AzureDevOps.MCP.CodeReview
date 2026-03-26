using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Services.LocalReview;
using REBUSS.Pure.Tools;

namespace REBUSS.Pure.Tests.Tools;

public class GetLocalChangesFilesToolHandlerTests
{
    private readonly ILocalReviewProvider _reviewProvider = Substitute.For<ILocalReviewProvider>();
    private readonly GetLocalChangesFilesToolHandler _handler;

    public GetLocalChangesFilesToolHandlerTests()
    {
        _handler = new GetLocalChangesFilesToolHandler(
            _reviewProvider,
            NullLogger<GetLocalChangesFilesToolHandler>.Instance);
    }

    private static LocalReviewFiles SampleFiles(int count = 1) => new()
    {
        RepositoryRoot = "/repo",
        Scope = "working-tree",
        CurrentBranch = "feature/x",
        Files = Enumerable.Range(0, count).Select(i => new PullRequestFileInfo
        {
            Path = $"src/File{i}.cs",
            Status = "modified",
            Additions = i,
            Deletions = i,
            Changes = i * 2,
            Extension = ".cs",
            ReviewPriority = "high"
        }).ToList(),
        Summary = new PullRequestFilesSummary { SourceFiles = count, HighPriorityFiles = count }
    };

    // --- Happy path ---

    [Fact]
    public async Task ExecuteAsync_ReturnsStructuredJson()
    {
        _reviewProvider.GetFilesAsync(Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(SampleFiles(2));

        var result = await _handler.ExecuteAsync(null);

        Assert.False(result.IsError);
        var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("/repo", doc.RootElement.GetProperty("repositoryRoot").GetString());
        Assert.Equal("working-tree", doc.RootElement.GetProperty("scope").GetString());
        Assert.Equal("feature/x", doc.RootElement.GetProperty("currentBranch").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("totalFiles").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("files").GetArrayLength());
    }

    [Fact]
    public async Task ExecuteAsync_DefaultsToWorkingTree_WhenNoScope()
    {
        _reviewProvider.GetFilesAsync(Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(SampleFiles());

        await _handler.ExecuteAsync(null);

        await _reviewProvider.Received(1).GetFilesAsync(
            Arg.Is<LocalReviewScope>(s => s.Kind == LocalReviewScopeKind.WorkingTree),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ParsesStagedScope()
    {
        _reviewProvider.GetFilesAsync(Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(SampleFiles());

        var args = new Dictionary<string, object> { ["scope"] = "staged" };
        await _handler.ExecuteAsync(args);

        await _reviewProvider.Received(1).GetFilesAsync(
            Arg.Is<LocalReviewScope>(s => s.Kind == LocalReviewScopeKind.Staged),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ParsesBranchDiffScope()
    {
        _reviewProvider.GetFilesAsync(Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(SampleFiles());

        var args = new Dictionary<string, object> { ["scope"] = "main" };
        await _handler.ExecuteAsync(args);

        await _reviewProvider.Received(1).GetFilesAsync(
            Arg.Is<LocalReviewScope>(s => s.Kind == LocalReviewScopeKind.BranchDiff && s.BaseBranch == "main"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_HandlesScopeAsJsonElement()
    {
        _reviewProvider.GetFilesAsync(Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(SampleFiles());

        var json = JsonSerializer.Deserialize<Dictionary<string, object>>("""{"scope":"staged"}""")!;
        await _handler.ExecuteAsync(json);

        await _reviewProvider.Received(1).GetFilesAsync(
            Arg.Is<LocalReviewScope>(s => s.Kind == LocalReviewScopeKind.Staged),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsSummaryInOutput()
    {
        _reviewProvider.GetFilesAsync(Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(SampleFiles(3));

        var result = await _handler.ExecuteAsync(null);

        var doc = JsonDocument.Parse(result.Content[0].Text);
        var summary = doc.RootElement.GetProperty("summary");
        Assert.Equal(3, summary.GetProperty("sourceFiles").GetInt32());
    }

    // --- Error cases ---

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenRepositoryNotFound()
    {
        _reviewProvider.GetFilesAsync(Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new LocalRepositoryNotFoundException("No repo found"));

        var result = await _handler.ExecuteAsync(null);

        Assert.True(result.IsError);
        Assert.Contains("Repository not found", result.Content[0].Text);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_OnUnexpectedException()
    {
        _reviewProvider.GetFilesAsync(Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await _handler.ExecuteAsync(null);

        Assert.True(result.IsError);
        Assert.Contains("boom", result.Content[0].Text);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenGitCommandFails()
    {
        _reviewProvider.GetFilesAsync(Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new GitCommandException(128, "fatal: not a git repository"));

        var result = await _handler.ExecuteAsync(null);

        Assert.True(result.IsError);
        Assert.Contains("Git command failed", result.Content[0].Text);
    }

    // --- Tool definition ---

    [Fact]
    public void GetToolDefinition_HasCorrectName()
    {
        Assert.Equal("get_local_files", _handler.ToolName);
    }

    [Fact]
    public void GetToolDefinition_HasScopeProperty()
    {
        var def = _handler.GetToolDefinition();
        Assert.True(def.InputSchema.Properties.ContainsKey("scope"));
    }

    [Fact]
    public void GetToolDefinition_ScopeIsNotRequired()
    {
        var def = _handler.GetToolDefinition();
        Assert.DoesNotContain("scope", def.InputSchema.Required!);
    }
}
