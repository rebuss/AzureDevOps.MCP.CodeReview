using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Services.LocalReview;

namespace REBUSS.Pure.Tests.Services.LocalReview;

public class LocalReviewProviderTests
{
    private readonly IWorkspaceRootProvider _rootProvider = Substitute.For<IWorkspaceRootProvider>();
    private readonly ILocalGitClient _gitClient = Substitute.For<ILocalGitClient>();
    private readonly LocalReviewProvider _provider;

    private const string RepoRoot = "/repo";

    public LocalReviewProviderTests()
    {
        _rootProvider.ResolveRepositoryRoot().Returns(RepoRoot);

        // Default: empty diff so tests that only exercise GetFilesAsync don't trip the
        // GetAllFileDiffsAsync code path with a NSubstitute null result.
        _gitClient.GetUnifiedDiffAsync(RepoRoot, Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(string.Empty);

        _provider = new LocalReviewProvider(
            _rootProvider,
            _gitClient,
            new FileClassifier(),
            NullLogger<LocalReviewProvider>.Instance);
    }

    // --- GetFilesAsync ---

    [Fact]
    public async Task GetFilesAsync_ReturnsCorrectRepositoryRoot()
    {
        _gitClient.GetCurrentBranchAsync(RepoRoot, Arg.Any<CancellationToken>()).Returns("main");
        _gitClient.GetChangedFilesAsync(RepoRoot, Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(new List<LocalFileStatus> { new('M', "src/Service.cs") });

        var result = await _provider.GetFilesAsync(LocalReviewScope.WorkingTree());

        Assert.Equal(RepoRoot, result.RepositoryRoot);
        Assert.Equal("main", result.CurrentBranch);
    }

    [Fact]
    public async Task GetFilesAsync_MapsStatusCorrectly()
    {
        _gitClient.GetCurrentBranchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        _gitClient.GetChangedFilesAsync(RepoRoot, Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(new List<LocalFileStatus>
            {
                new('A', "src/New.cs"),
                new('M', "src/Modified.cs"),
                new('D', "src/Deleted.cs"),
                new('R', "src/Renamed.cs", "src/Old.cs"),
                new('?', "src/Untracked.cs")
            });

        var result = await _provider.GetFilesAsync(LocalReviewScope.WorkingTree());

        Assert.Equal(5, result.Files.Count);
        Assert.Equal("added",    result.Files[0].Status);
        Assert.Equal("modified", result.Files[1].Status);
        Assert.Equal("removed",  result.Files[2].Status);
        Assert.Equal("renamed",  result.Files[3].Status);
        Assert.Equal("added",    result.Files[4].Status);  // untracked treated as added
    }

    [Fact]
    public async Task GetFilesAsync_ClassifiesFilesCorrectly()
    {
        _gitClient.GetCurrentBranchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        _gitClient.GetChangedFilesAsync(RepoRoot, Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(new List<LocalFileStatus>
            {
                new('M', "src/App.cs"),
                new('M', "tests/AppTests.cs"),
                new('M', "appsettings.json"),
                new('M', "docs/readme.md"),
                new('M', "lib/tool.dll")
            });

        var result = await _provider.GetFilesAsync(LocalReviewScope.WorkingTree());

        Assert.Equal(5, result.Files.Count);
        Assert.Equal(1, result.Summary.SourceFiles);
        Assert.Equal(1, result.Summary.TestFiles);
        Assert.Equal(1, result.Summary.ConfigFiles);
        Assert.Equal(1, result.Summary.DocsFiles);
        Assert.Equal(1, result.Summary.BinaryFiles);
    }

    [Fact]
    public async Task GetFilesAsync_StripsLeadingSlash()
    {
        _gitClient.GetCurrentBranchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        _gitClient.GetChangedFilesAsync(RepoRoot, Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(new List<LocalFileStatus> { new('M', "/src/App.cs") });

        var result = await _provider.GetFilesAsync(LocalReviewScope.WorkingTree());

        Assert.Equal("src/App.cs", result.Files[0].Path);
    }

    [Fact]
    public async Task GetFilesAsync_HandlesEmptyChanges()
    {
        _gitClient.GetCurrentBranchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("main");
        _gitClient.GetChangedFilesAsync(RepoRoot, Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(new List<LocalFileStatus>());

        var result = await _provider.GetFilesAsync(LocalReviewScope.WorkingTree());

        Assert.Empty(result.Files);
        Assert.Equal(0, result.Summary.SourceFiles);
    }

    [Fact]
    public async Task GetFilesAsync_ThrowsLocalRepositoryNotFoundException_WhenRootNotResolved()
    {
        _rootProvider.ResolveRepositoryRoot().Returns((string?)null);

        await Assert.ThrowsAsync<LocalRepositoryNotFoundException>(
            () => _provider.GetFilesAsync(LocalReviewScope.WorkingTree()));
    }

    [Fact]
    public async Task GetFilesAsync_PassesScopeToGitClient()
    {
        var scope = LocalReviewScope.Staged();
        _gitClient.GetCurrentBranchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("main");
        _gitClient.GetChangedFilesAsync(RepoRoot, scope, Arg.Any<CancellationToken>())
            .Returns(new List<LocalFileStatus>());

        await _provider.GetFilesAsync(scope);

        await _gitClient.Received(1).GetChangedFilesAsync(RepoRoot, scope, Arg.Any<CancellationToken>());
    }

    // --- GetAllFileDiffsAsync ---

    [Fact]
    public async Task GetAllFileDiffsAsync_SingleInvocationToGitClient_ReturnsParsedFiles()
    {
        const string diff =
            "diff --git a/src/A.cs b/src/A.cs\n" +
            "--- a/src/A.cs\n" +
            "+++ b/src/A.cs\n" +
            "@@ -1 +1 @@\n" +
            "-old\n" +
            "+new\n" +
            "diff --git a/src/B.cs b/src/B.cs\n" +
            "--- a/src/B.cs\n" +
            "+++ b/src/B.cs\n" +
            "@@ -1 +1 @@\n" +
            "-x\n" +
            "+y";
        _gitClient.GetUnifiedDiffAsync(RepoRoot, Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(diff);

        var result = await _provider.GetAllFileDiffsAsync(LocalReviewScope.Staged());

        Assert.Equal(2, result.Files.Count);
        Assert.Equal("src/A.cs", result.Files[0].Path);
        Assert.Equal("src/B.cs", result.Files[1].Path);
        await _gitClient.Received(1).GetUnifiedDiffAsync(
            RepoRoot, Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAllFileDiffsAsync_AppliesGeneratedSkipReason()
    {
        const string diff =
            "diff --git a/obj/Debug/net8.0/out.cs b/obj/Debug/net8.0/out.cs\n" +
            "--- a/obj/Debug/net8.0/out.cs\n" +
            "+++ b/obj/Debug/net8.0/out.cs\n" +
            "@@ -1 +1 @@\n" +
            "-x\n" +
            "+y";
        _gitClient.GetUnifiedDiffAsync(RepoRoot, Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(diff);

        var result = await _provider.GetAllFileDiffsAsync(LocalReviewScope.Staged());

        Assert.Single(result.Files);
        Assert.Equal("generated file", result.Files[0].SkipReason);
        Assert.Empty(result.Files[0].Hunks);
    }

    [Fact]
    public async Task GetAllFileDiffsAsync_PreservesGitBinarySkipReason()
    {
        const string diff =
            "diff --git a/img.png b/img.png\n" +
            "Binary files a/img.png and b/img.png differ";
        _gitClient.GetUnifiedDiffAsync(RepoRoot, Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(diff);

        var result = await _provider.GetAllFileDiffsAsync(LocalReviewScope.Staged());

        Assert.Equal("binary file", result.Files[0].SkipReason);
    }

    [Fact]
    public async Task GetAllFileDiffsAsync_EmptyDiff_ReturnsZeroFiles()
    {
        _gitClient.GetUnifiedDiffAsync(RepoRoot, Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(string.Empty);

        var result = await _provider.GetAllFileDiffsAsync(LocalReviewScope.Staged());

        Assert.Empty(result.Files);
    }

    [Fact]
    public async Task GetAllFileDiffsAsync_ThrowsLocalRepositoryNotFoundException_WhenRootNotResolved()
    {
        _rootProvider.ResolveRepositoryRoot().Returns((string?)null);

        await Assert.ThrowsAsync<LocalRepositoryNotFoundException>(
            () => _provider.GetAllFileDiffsAsync(LocalReviewScope.WorkingTree()));
    }

    // --- GetFileDiffAsync (now layered on GetAllFileDiffsAsync) ---

    [Fact]
    public async Task GetFileDiffAsync_ReturnsDiff_ForMatchingFile()
    {
        const string diff =
            "diff --git a/src/Service.cs b/src/Service.cs\n" +
            "--- a/src/Service.cs\n" +
            "+++ b/src/Service.cs\n" +
            "@@ -1 +1 @@\n" +
            "-old line\n" +
            "+new line";
        _gitClient.GetUnifiedDiffAsync(RepoRoot, Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(diff);

        var result = await _provider.GetFileDiffAsync("src/Service.cs", LocalReviewScope.WorkingTree());

        Assert.Single(result.Files);
        Assert.Equal("src/Service.cs", result.Files[0].Path);
        var allLines = result.Files[0].Hunks.SelectMany(h => h.Lines).ToList();
        Assert.Contains(allLines, l => l.Op == '-' && l.Text == "old line");
        Assert.Contains(allLines, l => l.Op == '+' && l.Text == "new line");
    }

    [Fact]
    public async Task GetFileDiffAsync_IsCaseInsensitive()
    {
        const string diff =
            "diff --git a/src/Service.cs b/src/Service.cs\n" +
            "--- a/src/Service.cs\n" +
            "+++ b/src/Service.cs\n" +
            "@@ -1 +1 @@\n" +
            "-x\n" +
            "+y";
        _gitClient.GetUnifiedDiffAsync(RepoRoot, Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(diff);

        var result = await _provider.GetFileDiffAsync("SRC/SERVICE.CS", LocalReviewScope.WorkingTree());

        Assert.Single(result.Files);
    }

    [Fact]
    public async Task GetFileDiffAsync_ThrowsLocalFileNotFoundException_WhenNotInDiff()
    {
        _gitClient.GetUnifiedDiffAsync(RepoRoot, Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(string.Empty);

        await Assert.ThrowsAsync<LocalFileNotFoundException>(
            () => _provider.GetFileDiffAsync("src/NotChanged.cs", LocalReviewScope.WorkingTree()));
    }

    [Fact]
    public async Task GetFileDiffAsync_ThrowsLocalRepositoryNotFoundException_WhenRootNotResolved()
    {
        _rootProvider.ResolveRepositoryRoot().Returns((string?)null);

        await Assert.ThrowsAsync<LocalRepositoryNotFoundException>(
            () => _provider.GetFileDiffAsync("src/Service.cs", LocalReviewScope.WorkingTree()));
    }

    [Fact]
    public async Task GetFileDiffAsync_SetsChangeType_Delete_ForDeletedFile()
    {
        const string diff =
            "diff --git a/src/Deleted.cs b/src/Deleted.cs\n" +
            "deleted file mode 100644\n" +
            "--- a/src/Deleted.cs\n" +
            "+++ /dev/null\n" +
            "@@ -1 +0,0 @@\n" +
            "-bye";
        _gitClient.GetUnifiedDiffAsync(RepoRoot, Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(diff);

        var result = await _provider.GetFileDiffAsync("src/Deleted.cs", LocalReviewScope.WorkingTree());

        // Deleted files keep their hunks (the review can still see what was removed).
        // ChangeType signals the deletion to downstream consumers.
        Assert.Equal("delete", result.Files[0].ChangeType);
    }

    [Fact]
    public async Task GetFileDiffAsync_SetsSkipReason_ForBinaryFile()
    {
        const string diff =
            "diff --git a/lib/tool.dll b/lib/tool.dll\n" +
            "--- a/lib/tool.dll\n" +
            "+++ b/lib/tool.dll\n" +
            "@@ -1 +1 @@\n" +
            "-x\n" +
            "+y";
        _gitClient.GetUnifiedDiffAsync(RepoRoot, Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(diff);

        var result = await _provider.GetFileDiffAsync("lib/tool.dll", LocalReviewScope.WorkingTree());

        Assert.Equal("binary file", result.Files[0].SkipReason);
        Assert.Empty(result.Files[0].Hunks);
    }

    [Fact]
    public async Task GetFileDiffAsync_SetsSkipReason_ForGeneratedFile()
    {
        const string diff =
            "diff --git a/obj/Debug/net8.0/out.cs b/obj/Debug/net8.0/out.cs\n" +
            "--- a/obj/Debug/net8.0/out.cs\n" +
            "+++ b/obj/Debug/net8.0/out.cs\n" +
            "@@ -1 +1 @@\n" +
            "-x\n" +
            "+y";
        _gitClient.GetUnifiedDiffAsync(RepoRoot, Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(diff);

        var result = await _provider.GetFileDiffAsync("obj/Debug/net8.0/out.cs", LocalReviewScope.WorkingTree());

        Assert.Equal("generated file", result.Files[0].SkipReason);
        Assert.Empty(result.Files[0].Hunks);
    }

    [Fact]
    public async Task GetFileDiffAsync_PassesScopeToUnifiedDiff()
    {
        var scope = LocalReviewScope.Staged();
        _gitClient.GetUnifiedDiffAsync(RepoRoot, scope, Arg.Any<CancellationToken>())
            .Returns(
                "diff --git a/src/Service.cs b/src/Service.cs\n" +
                "--- a/src/Service.cs\n" +
                "+++ b/src/Service.cs\n" +
                "@@ -1 +1 @@\n" +
                "-base\n" +
                "+staged");

        await _provider.GetFileDiffAsync("src/Service.cs", scope);

        await _gitClient.Received(1).GetUnifiedDiffAsync(RepoRoot, scope, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetFileDiffAsync_AfterGetAllFileDiffs_ServesFromCache_WithoutSecondGitCall()
    {
        const string diff =
            "diff --git a/src/A.cs b/src/A.cs\n" +
            "--- a/src/A.cs\n" +
            "+++ b/src/A.cs\n" +
            "@@ -1 +1 @@\n" +
            "-old\n" +
            "+new\n" +
            "diff --git a/src/B.cs b/src/B.cs\n" +
            "--- a/src/B.cs\n" +
            "+++ b/src/B.cs\n" +
            "@@ -1 +1 @@\n" +
            "-x\n" +
            "+y";
        var scope = LocalReviewScope.WorkingTree();
        _gitClient.GetUnifiedDiffAsync(RepoRoot, Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(diff);

        // Prime the cache via the orchestrator-style aggregate call.
        _ = await _provider.GetAllFileDiffsAsync(scope);

        // Two single-file lookups against the same scope must hit the cache; only the
        // initial GetAllFileDiffsAsync should reach git.
        var a = await _provider.GetFileDiffAsync("src/A.cs", scope);
        var b = await _provider.GetFileDiffAsync("src/B.cs", scope);

        Assert.Equal("src/A.cs", a.Files[0].Path);
        Assert.Equal("src/B.cs", b.Files[0].Path);
        await _gitClient.Received(1).GetUnifiedDiffAsync(
            RepoRoot, Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetFileDiffAsync_DoesNotReuseCache_AcrossDifferentScopes()
    {
        const string diff =
            "diff --git a/src/A.cs b/src/A.cs\n" +
            "--- a/src/A.cs\n" +
            "+++ b/src/A.cs\n" +
            "@@ -1 +1 @@\n" +
            "-old\n" +
            "+new";
        _gitClient.GetUnifiedDiffAsync(RepoRoot, Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(diff);

        _ = await _provider.GetAllFileDiffsAsync(LocalReviewScope.WorkingTree());
        _ = await _provider.GetFileDiffAsync("src/A.cs", LocalReviewScope.Staged());

        // Different scope → cache miss → second git invocation expected.
        await _gitClient.Received(2).GetUnifiedDiffAsync(
            RepoRoot, Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetFileDiffAsync_SetsAdditionsAndDeletions()
    {
        const string diff =
            "diff --git a/src/Service.cs b/src/Service.cs\n" +
            "--- a/src/Service.cs\n" +
            "+++ b/src/Service.cs\n" +
            "@@ -1 +1 @@\n" +
            "-old line\n" +
            "+new line";
        _gitClient.GetUnifiedDiffAsync(RepoRoot, Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(diff);

        var result = await _provider.GetFileDiffAsync("src/Service.cs", LocalReviewScope.WorkingTree());

        Assert.Equal(1, result.Files[0].Additions);
        Assert.Equal(1, result.Files[0].Deletions);
    }

    // --- GetSkipReason unit tests ---

    [Fact]
    public void GetSkipReason_ReturnsFileDeleted_ForDeletedStatus()
    {
        var status = new LocalFileStatus('D', "src/File.cs");
        Assert.Equal("file deleted", _provider.GetSkipReason(status));
    }

    [Fact]
    public void GetSkipReason_ReturnsFileRenamed_ForRenameStatus()
    {
        var status = new LocalFileStatus('R', "src/New.cs", "src/Old.cs");
        Assert.Equal("file renamed", _provider.GetSkipReason(status));
    }

    [Fact]
    public void GetSkipReason_ReturnsNull_ForModifiedFile()
    {
        var status = new LocalFileStatus('M', "src/Service.cs");
        Assert.Null(_provider.GetSkipReason(status));
    }

    [Fact]
    public void GetSkipReason_ReturnsNull_ForAddedFile()
    {
        var status = new LocalFileStatus('A', "src/New.cs");
        Assert.Null(_provider.GetSkipReason(status));
    }

    [Fact]
    public void GetSkipReason_ReturnsBinaryFile_ForBinaryExtension()
    {
        var status = new LocalFileStatus('M', "lib/tool.dll");
        Assert.Equal("binary file", _provider.GetSkipReason(status));
    }

    [Fact]
    public void GetSkipReason_ReturnsGeneratedFile_ForGeneratedPath()
    {
        var status = new LocalFileStatus('M', "/obj/Debug/net8.0/out.cs");
        Assert.Equal("generated file", _provider.GetSkipReason(status));
    }
}
