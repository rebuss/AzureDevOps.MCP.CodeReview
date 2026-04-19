using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using REBUSS.Pure.GitHub.Api;
using REBUSS.Pure.GitHub.Parsers;
using REBUSS.Pure.GitHub.Providers;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Shared;

namespace REBUSS.Pure.GitHub.Tests.Providers;

public class GitHubDiffProviderTests
{
    private readonly IGitHubApiClient _apiClient = Substitute.For<IGitHubApiClient>();
    private readonly GitHubDiffProvider _provider;

    private const string PrDetailsJson = """
        {
            "number": 42,
            "title": "Fix bug #42",
            "state": "open",
            "draft": false,
            "head": { "ref": "feature/fix-42", "sha": "aaa111" },
            "base": { "ref": "main", "sha": "bbb222" },
            "user": { "login": "dev-user" }
        }
        """;

    private const string FilesJson = """
        [
            {
                "filename": "src/File.cs",
                "status": "modified",
                "additions": 5,
                "deletions": 3
            }
        ]
        """;

    public GitHubDiffProviderTests()
    {
        _provider = new GitHubDiffProvider(
            _apiClient,
            new GitHubPullRequestParser(NullLogger<GitHubPullRequestParser>.Instance),
            new GitHubFileChangesParser(NullLogger<GitHubFileChangesParser>.Instance),
            new StructuredDiffBuilder(new DiffPlexDiffAlgorithm(), NullLogger<StructuredDiffBuilder>.Instance),
            new FileClassifier(),
            NullLogger<GitHubDiffProvider>.Instance);
    }

    [Fact]
    public async Task GetDiffAsync_ReturnsDiff_WithCorrectMetadata()
    {
        SetupStandardMocks();
        _apiClient.GetFileContentAtRefAsync("bbb222", "src/File.cs", Arg.Any<CancellationToken>()).Returns("old line");
        _apiClient.GetFileContentAtRefAsync("aaa111", "src/File.cs", Arg.Any<CancellationToken>()).Returns("new line");

        var result = await _provider.GetDiffAsync(42);

        Assert.Equal("Fix bug #42", result.Title);
        Assert.Equal("active", result.Status);
        Assert.Equal("feature/fix-42", result.SourceBranch);
        Assert.Equal("main", result.TargetBranch);
        Assert.Single(result.Files);
        Assert.Equal("src/File.cs", result.Files[0].Path);
        Assert.Equal("edit", result.Files[0].ChangeType);
    }

    [Fact]
    public async Task GetDiffAsync_GeneratesStructuredDiff_ForModifiedFile()
    {
        SetupStandardMocks();
        _apiClient.GetFileContentAtRefAsync("bbb222", "src/File.cs", Arg.Any<CancellationToken>()).Returns("old line");
        _apiClient.GetFileContentAtRefAsync("aaa111", "src/File.cs", Arg.Any<CancellationToken>()).Returns("new line");

        var result = await _provider.GetDiffAsync(42);

        Assert.NotEmpty(result.Files[0].Hunks);
        var allLines = result.Files[0].Hunks.SelectMany(h => h.Lines).ToList();
        Assert.Contains(allLines, l => l.Op == '-' && l.Text == "old line");
        Assert.Contains(allLines, l => l.Op == '+' && l.Text == "new line");
    }

    [Fact]
    public async Task GetDiffAsync_SetsAdditionsAndDeletions()
    {
        SetupStandardMocks();
        _apiClient.GetFileContentAtRefAsync("bbb222", "src/File.cs", Arg.Any<CancellationToken>()).Returns("old line");
        _apiClient.GetFileContentAtRefAsync("aaa111", "src/File.cs", Arg.Any<CancellationToken>()).Returns("new line");

        var result = await _provider.GetDiffAsync(42);

        Assert.Equal(1, result.Files[0].Additions);
        Assert.Equal(1, result.Files[0].Deletions);
    }

    [Fact]
    public async Task GetDiffAsync_ThrowsPullRequestNotFound_On404()
    {
        _apiClient.GetPullRequestDetailsAsync(999)
            .ThrowsAsync(new HttpRequestException("Not Found", null, HttpStatusCode.NotFound));

        await Assert.ThrowsAsync<PullRequestNotFoundException>(
            () => _provider.GetDiffAsync(999));
    }

    [Fact]
    public async Task GetDiffAsync_HandlesNoFiles()
    {
        _apiClient.GetPullRequestDetailsAsync(42).Returns(PrDetailsJson);
        _apiClient.GetPullRequestFilesAsync(42).Returns("[]");

        var result = await _provider.GetDiffAsync(42);

        Assert.Empty(result.Files);
    }

    [Fact]
    public async Task GetDiffAsync_RespectsCancellation()
    {
        SetupStandardMocks();
        _apiClient.GetFileContentAtRefAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("x");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _provider.GetDiffAsync(42, cts.Token));
    }

    // --- GetFileDiffAsync ---

    [Fact]
    public async Task GetFileDiffAsync_ReturnsDiff_ForMatchingFile()
    {
        SetupStandardMocks();
        _apiClient.GetFileContentAtRefAsync("bbb222", "src/File.cs", Arg.Any<CancellationToken>()).Returns("old line");
        _apiClient.GetFileContentAtRefAsync("aaa111", "src/File.cs", Arg.Any<CancellationToken>()).Returns("new line");

        var result = await _provider.GetFileDiffAsync(42, "src/File.cs");

        Assert.Equal("Fix bug #42", result.Title);
        Assert.Single(result.Files);
        Assert.Equal("src/File.cs", result.Files[0].Path);
        var allLines = result.Files[0].Hunks.SelectMany(h => h.Lines).ToList();
        Assert.Contains(allLines, l => l.Op == '-' && l.Text == "old line");
        Assert.Contains(allLines, l => l.Op == '+' && l.Text == "new line");
    }

    [Fact]
    public async Task GetFileDiffAsync_ThrowsFileNotFound_WhenPathDoesNotExist()
    {
        SetupStandardMocks();

        await Assert.ThrowsAsync<FileNotFoundInPullRequestException>(
            () => _provider.GetFileDiffAsync(42, "src/NonExistent.cs"));
    }

    [Fact]
    public async Task GetFileDiffAsync_ThrowsPullRequestNotFound_On404()
    {
        _apiClient.GetPullRequestDetailsAsync(999)
            .ThrowsAsync(new HttpRequestException("Not Found", null, HttpStatusCode.NotFound));

        await Assert.ThrowsAsync<PullRequestNotFoundException>(
            () => _provider.GetFileDiffAsync(999, "src/File.cs"));
    }

    [Fact]
    public async Task GetFileDiffAsync_IsCaseInsensitive()
    {
        SetupStandardMocks();
        _apiClient.GetFileContentAtRefAsync("bbb222", "src/File.cs", Arg.Any<CancellationToken>()).Returns("old");
        _apiClient.GetFileContentAtRefAsync("aaa111", "src/File.cs", Arg.Any<CancellationToken>()).Returns("new");

        var result = await _provider.GetFileDiffAsync(42, "SRC/FILE.CS");

        Assert.Single(result.Files);
    }

    // --- Diff skip scenarios ---

    [Theory]
    [InlineData("delete", "file deleted")]
    [InlineData("rename", "file renamed")]
    public async Task GetDiffAsync_SkipsDiff_ForChangeType(string changeType, string expectedSkipReason)
    {
        _apiClient.GetPullRequestDetailsAsync(42).Returns(PrDetailsJson);
        _apiClient.GetPullRequestFilesAsync(42).Returns($$"""
            [
                { "filename": "src/File.cs", "status": "{{changeType}}" }
            ]
            """);

        var result = await _provider.GetDiffAsync(42);

        Assert.Single(result.Files);
        Assert.Equal(expectedSkipReason, result.Files[0].SkipReason);
        Assert.Empty(result.Files[0].Hunks);
    }

    [Theory]
    [InlineData("lib/external.dll")]
    [InlineData("images/photo.jpg")]
    [InlineData("packages/archive.zip")]
    public async Task GetDiffAsync_SkipsDiff_ForBinaryFiles(string path)
    {
        _apiClient.GetPullRequestDetailsAsync(42).Returns(PrDetailsJson);
        _apiClient.GetPullRequestFilesAsync(42).Returns($$"""
            [
                { "filename": "{{path}}", "status": "modified" }
            ]
            """);

        var result = await _provider.GetDiffAsync(42);

        Assert.Equal("binary file", result.Files[0].SkipReason);
    }

    [Fact]
    public async Task GetDiffAsync_SkipsDiff_ForFullFileRewrite()
    {
        SetupStandardMocks();

        var oldContent = string.Join("\n", Enumerable.Range(1, 15).Select(i => $"old line {i}"));
        var newContent = string.Join("\n", Enumerable.Range(1, 15).Select(i => $"new line {i}"));
        _apiClient.GetFileContentAtRefAsync("bbb222", "src/File.cs", Arg.Any<CancellationToken>()).Returns(oldContent);
        _apiClient.GetFileContentAtRefAsync("aaa111", "src/File.cs", Arg.Any<CancellationToken>()).Returns(newContent);

        var result = await _provider.GetDiffAsync(42);

        Assert.Equal("full file rewrite", result.Files[0].SkipReason);
        Assert.Empty(result.Files[0].Hunks);
    }

    // --- Feature 015: Skip base ref fetch for added files ---

    [Fact]
    public async Task GetDiffAsync_AddedFile_FetchesHeadOnly_NotBase()
    {
        _apiClient.GetPullRequestDetailsAsync(42, Arg.Any<CancellationToken>()).Returns(PrDetailsJson);
        _apiClient.GetPullRequestFilesAsync(42, Arg.Any<CancellationToken>()).Returns("""
            [{ "filename": "src/New.cs", "status": "added", "additions": 5, "deletions": 0 }]
            """);
        _apiClient.GetFileContentAtRefAsync("aaa111", "src/New.cs", Arg.Any<CancellationToken>()).Returns("class New { }");

        await _provider.GetDiffAsync(42);

        // Head ref fetched
        await _apiClient.Received(1).GetFileContentAtRefAsync("aaa111", "src/New.cs", Arg.Any<CancellationToken>());
        // Base ref NOT fetched
        await _apiClient.DidNotReceive().GetFileContentAtRefAsync("bbb222", "src/New.cs", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetDiffAsync_EditedFile_FetchesBothBaseAndHead()
    {
        SetupStandardMocks();
        _apiClient.GetFileContentAtRefAsync("bbb222", "src/File.cs", Arg.Any<CancellationToken>()).Returns("old");
        _apiClient.GetFileContentAtRefAsync("aaa111", "src/File.cs", Arg.Any<CancellationToken>()).Returns("new");

        await _provider.GetDiffAsync(42);

        // Both base and head fetched
        await _apiClient.Received(1).GetFileContentAtRefAsync("bbb222", "src/File.cs", Arg.Any<CancellationToken>());
        await _apiClient.Received(1).GetFileContentAtRefAsync("aaa111", "src/File.cs", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetDiffAsync_MixedFiles_CorrectFetchCounts_AndAddedFileHasOnlyAdditions()
    {
        _apiClient.GetPullRequestDetailsAsync(42, Arg.Any<CancellationToken>()).Returns(PrDetailsJson);
        _apiClient.GetPullRequestFilesAsync(42, Arg.Any<CancellationToken>()).Returns("""
            [
                { "filename": "src/Add1.cs", "status": "added", "additions": 1, "deletions": 0 },
                { "filename": "src/Add2.cs", "status": "added", "additions": 1, "deletions": 0 },
                { "filename": "src/Add3.cs", "status": "added", "additions": 1, "deletions": 0 },
                { "filename": "src/Edit1.cs", "status": "modified", "additions": 1, "deletions": 1 },
                { "filename": "src/Edit2.cs", "status": "modified", "additions": 1, "deletions": 1 },
                { "filename": "src/Del.cs", "status": "removed", "additions": 0, "deletions": 5 }
            ]
            """);

        // Setup returns for edit files (both base + head)
        _apiClient.GetFileContentAtRefAsync("bbb222", "src/Edit1.cs", Arg.Any<CancellationToken>()).Returns("old1");
        _apiClient.GetFileContentAtRefAsync("aaa111", "src/Edit1.cs", Arg.Any<CancellationToken>()).Returns("new1");
        _apiClient.GetFileContentAtRefAsync("bbb222", "src/Edit2.cs", Arg.Any<CancellationToken>()).Returns("old2");
        _apiClient.GetFileContentAtRefAsync("aaa111", "src/Edit2.cs", Arg.Any<CancellationToken>()).Returns("new2");
        // Setup returns for add files (head only)
        _apiClient.GetFileContentAtRefAsync("aaa111", "src/Add1.cs", Arg.Any<CancellationToken>()).Returns("class Add1 { }");
        _apiClient.GetFileContentAtRefAsync("aaa111", "src/Add2.cs", Arg.Any<CancellationToken>()).Returns("class Add2 { }");
        _apiClient.GetFileContentAtRefAsync("aaa111", "src/Add3.cs", Arg.Any<CancellationToken>()).Returns("class Add3 { }");

        var result = await _provider.GetDiffAsync(42);

        // (a) Call counts: 2 base (edits only) + 5 head (3 add + 2 edit) = 7 total
        var totalCalls = _apiClient.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(IGitHubApiClient.GetFileContentAtRefAsync));
        Assert.Equal(7, totalCalls);

        // No base fetch for added files
        await _apiClient.DidNotReceive().GetFileContentAtRefAsync("bbb222", "src/Add1.cs", Arg.Any<CancellationToken>());
        await _apiClient.DidNotReceive().GetFileContentAtRefAsync("bbb222", "src/Add2.cs", Arg.Any<CancellationToken>());
        await _apiClient.DidNotReceive().GetFileContentAtRefAsync("bbb222", "src/Add3.cs", Arg.Any<CancellationToken>());

        // (b) Added files have only '+' lines (FR-005/SC-004 byte-identical output)
        var addFile = result.Files.First(f => f.Path == "src/Add1.cs");
        Assert.NotEmpty(addFile.Hunks);
        var addLines = addFile.Hunks.SelectMany(h => h.Lines).ToList();
        Assert.All(addLines, l => Assert.Equal('+', l.Op));

        // Delete file is skipped entirely
        var delFile = result.Files.First(f => f.Path == "src/Del.cs");
        Assert.NotNull(delFile.SkipReason);
    }

    // --- Patch fast path: GitHub returns the unified-diff `patch` field for each file,
    //     and the provider should consume it without making any per-file content fetches. ---

    [Fact]
    public async Task GetDiffAsync_UsesPatchField_NoContentFetches()
    {
        _apiClient.GetPullRequestDetailsAsync(42, Arg.Any<CancellationToken>()).Returns(PrDetailsJson);
        _apiClient.GetPullRequestFilesAsync(42, Arg.Any<CancellationToken>()).Returns("""
            [
                {
                    "filename": "src/File.cs",
                    "status": "modified",
                    "additions": 1,
                    "deletions": 1,
                    "patch": "@@ -1,1 +1,1 @@\n-old line\n+new line"
                }
            ]
            """);

        var result = await _provider.GetDiffAsync(42);

        // Hunks were built from the patch — content endpoint was never hit.
        await _apiClient.DidNotReceive().GetFileContentAtRefAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        Assert.Single(result.Files);
        Assert.Single(result.Files[0].Hunks);
        var lines = result.Files[0].Hunks[0].Lines;
        Assert.Equal('-', lines[0].Op);
        Assert.Equal("old line", lines[0].Text);
        Assert.Equal('+', lines[1].Op);
        Assert.Equal("new line", lines[1].Text);
    }

    [Fact]
    public async Task GetDiffAsync_ManyFilesWithPatch_ZeroContentFetches()
    {
        const string patch = "@@ -1,1 +1,1 @@\n-old\n+new";
        var fileEntries = string.Join(",", Enumerable.Range(1, 25).Select(i =>
            $$"""
              {
                  "filename": "src/F{{i}}.cs",
                  "status": "modified",
                  "additions": 1,
                  "deletions": 1,
                  "patch": "{{patch.Replace("\n", "\\n")}}"
              }
              """));
        _apiClient.GetPullRequestDetailsAsync(42, Arg.Any<CancellationToken>()).Returns(PrDetailsJson);
        _apiClient.GetPullRequestFilesAsync(42, Arg.Any<CancellationToken>()).Returns($"[{fileEntries}]");

        var result = await _provider.GetDiffAsync(42);

        Assert.Equal(25, result.Files.Count);
        await _apiClient.DidNotReceive().GetFileContentAtRefAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetDiffAsync_FallsBack_WhenPatchMissing()
    {
        // GitHub omits `patch` for binary or oversized diffs — provider must fall back
        // to fetching base + head and re-diffing.
        _apiClient.GetPullRequestDetailsAsync(42, Arg.Any<CancellationToken>()).Returns(PrDetailsJson);
        _apiClient.GetPullRequestFilesAsync(42, Arg.Any<CancellationToken>()).Returns("""
            [
                { "filename": "src/Huge.cs", "status": "modified", "additions": 0, "deletions": 0 }
            ]
            """);
        _apiClient.GetFileContentAtRefAsync("bbb222", "src/Huge.cs", Arg.Any<CancellationToken>()).Returns("old");
        _apiClient.GetFileContentAtRefAsync("aaa111", "src/Huge.cs", Arg.Any<CancellationToken>()).Returns("new");

        var result = await _provider.GetDiffAsync(42);

        await _apiClient.Received(1).GetFileContentAtRefAsync("bbb222", "src/Huge.cs", Arg.Any<CancellationToken>());
        await _apiClient.Received(1).GetFileContentAtRefAsync("aaa111", "src/Huge.cs", Arg.Any<CancellationToken>());
        Assert.Single(result.Files);
        Assert.NotEmpty(result.Files[0].Hunks);
    }

    [Fact]
    public async Task GetDiffAsync_MixedPatchAndNoPatch_FetchesOnlyMissing()
    {
        _apiClient.GetPullRequestDetailsAsync(42, Arg.Any<CancellationToken>()).Returns(PrDetailsJson);
        _apiClient.GetPullRequestFilesAsync(42, Arg.Any<CancellationToken>()).Returns("""
            [
                {
                    "filename": "src/WithPatch.cs",
                    "status": "modified",
                    "additions": 1,
                    "deletions": 1,
                    "patch": "@@ -1,1 +1,1 @@\n-old\n+new"
                },
                { "filename": "src/NoPatch.cs", "status": "modified", "additions": 0, "deletions": 0 }
            ]
            """);
        _apiClient.GetFileContentAtRefAsync("bbb222", "src/NoPatch.cs", Arg.Any<CancellationToken>()).Returns("old");
        _apiClient.GetFileContentAtRefAsync("aaa111", "src/NoPatch.cs", Arg.Any<CancellationToken>()).Returns("new");

        var result = await _provider.GetDiffAsync(42);

        // Patched file → no fetch
        await _apiClient.DidNotReceive().GetFileContentAtRefAsync(
            Arg.Any<string>(), "src/WithPatch.cs", Arg.Any<CancellationToken>());
        // Patchless file → both refs fetched
        await _apiClient.Received(1).GetFileContentAtRefAsync("bbb222", "src/NoPatch.cs", Arg.Any<CancellationToken>());
        await _apiClient.Received(1).GetFileContentAtRefAsync("aaa111", "src/NoPatch.cs", Arg.Any<CancellationToken>());
        Assert.Equal(2, result.Files.Count);
    }

    private void SetupStandardMocks()
    {
        _apiClient.GetPullRequestDetailsAsync(42, Arg.Any<CancellationToken>()).Returns(PrDetailsJson);
        _apiClient.GetPullRequestFilesAsync(42, Arg.Any<CancellationToken>()).Returns(FilesJson);
    }
}
