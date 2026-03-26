using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models;
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

        _provider = new LocalReviewProvider(
            _rootProvider,
            _gitClient,
            new StructuredDiffBuilder(new LcsDiffAlgorithm(), NullLogger<StructuredDiffBuilder>.Instance),
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

    // --- GetFileDiffAsync ---

    [Fact]
    public async Task GetFileDiffAsync_ReturnsDiff_ForMatchingFile()
    {
        _gitClient.GetChangedFilesAsync(RepoRoot, Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(new List<LocalFileStatus> { new('M', "src/Service.cs") });
        _gitClient.GetFileContentAtRefAsync(RepoRoot, "src/Service.cs", "HEAD", Arg.Any<CancellationToken>())
            .Returns("old line");
        _gitClient.GetFileContentAtRefAsync(RepoRoot, "src/Service.cs", LocalGitClient.WorkingTreeRef, Arg.Any<CancellationToken>())
            .Returns("new line");

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
        _gitClient.GetChangedFilesAsync(RepoRoot, Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(new List<LocalFileStatus> { new('M', "src/Service.cs") });
        _gitClient.GetFileContentAtRefAsync(RepoRoot, "src/Service.cs", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("content");

        var result = await _provider.GetFileDiffAsync("SRC/SERVICE.CS", LocalReviewScope.WorkingTree());

        Assert.Single(result.Files);
    }

    [Fact]
    public async Task GetFileDiffAsync_ThrowsLocalFileNotFoundException_WhenNotChanged()
    {
        _gitClient.GetChangedFilesAsync(RepoRoot, Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(new List<LocalFileStatus> { new('M', "src/Other.cs") });

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
    public async Task GetFileDiffAsync_SetsSkipReason_ForDeletedFile()
    {
        _gitClient.GetChangedFilesAsync(RepoRoot, Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(new List<LocalFileStatus> { new('D', "src/Deleted.cs") });

        var result = await _provider.GetFileDiffAsync("src/Deleted.cs", LocalReviewScope.WorkingTree());

        Assert.Equal("file deleted", result.Files[0].SkipReason);
        Assert.Empty(result.Files[0].Hunks);
    }

    [Fact]
    public async Task GetFileDiffAsync_SetsSkipReason_ForRenamedFile()
    {
        _gitClient.GetChangedFilesAsync(RepoRoot, Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(new List<LocalFileStatus> { new('R', "src/New.cs", "src/Old.cs") });

        var result = await _provider.GetFileDiffAsync("src/New.cs", LocalReviewScope.WorkingTree());

        Assert.Equal("file renamed", result.Files[0].SkipReason);
    }

    [Fact]
    public async Task GetFileDiffAsync_SetsSkipReason_ForBinaryFile()
    {
        _gitClient.GetChangedFilesAsync(RepoRoot, Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(new List<LocalFileStatus> { new('M', "lib/tool.dll") });

        var result = await _provider.GetFileDiffAsync("lib/tool.dll", LocalReviewScope.WorkingTree());

        Assert.Equal("binary file", result.Files[0].SkipReason);
        Assert.Empty(result.Files[0].Hunks);
    }

    [Fact]
    public async Task GetFileDiffAsync_SetsSkipReason_ForGeneratedFile()
    {
        _gitClient.GetChangedFilesAsync(RepoRoot, Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(new List<LocalFileStatus> { new('M', "/obj/Debug/net8.0/out.cs") });

        var result = await _provider.GetFileDiffAsync("/obj/Debug/net8.0/out.cs", LocalReviewScope.WorkingTree());

        Assert.Equal("generated file", result.Files[0].SkipReason);
        Assert.Empty(result.Files[0].Hunks);
    }

    [Fact]
    public async Task GetFileDiffAsync_UsesStagedRef_ForStagedScope()
    {
        _gitClient.GetChangedFilesAsync(RepoRoot, Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(new List<LocalFileStatus> { new('M', "src/Service.cs") });
        _gitClient.GetFileContentAtRefAsync(RepoRoot, "src/Service.cs", "HEAD", Arg.Any<CancellationToken>())
            .Returns("base");
        _gitClient.GetFileContentAtRefAsync(RepoRoot, "src/Service.cs", ":0", Arg.Any<CancellationToken>())
            .Returns("staged");

        await _provider.GetFileDiffAsync("src/Service.cs", LocalReviewScope.Staged());

        await _gitClient.Received(1).GetFileContentAtRefAsync(RepoRoot, "src/Service.cs", ":0", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetFileDiffAsync_UsesBranchRef_ForBranchDiffScope()
    {
        var scope = LocalReviewScope.BranchDiff("main");
        _gitClient.GetChangedFilesAsync(RepoRoot, scope, Arg.Any<CancellationToken>())
            .Returns(new List<LocalFileStatus> { new('M', "src/Service.cs") });
        _gitClient.GetFileContentAtRefAsync(RepoRoot, "src/Service.cs", "main", Arg.Any<CancellationToken>())
            .Returns("base");
        _gitClient.GetFileContentAtRefAsync(RepoRoot, "src/Service.cs", "HEAD", Arg.Any<CancellationToken>())
            .Returns("head");

        await _provider.GetFileDiffAsync("src/Service.cs", scope);

        await _gitClient.Received(1).GetFileContentAtRefAsync(RepoRoot, "src/Service.cs", "main", Arg.Any<CancellationToken>());
        await _gitClient.Received(1).GetFileContentAtRefAsync(RepoRoot, "src/Service.cs", "HEAD", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetFileDiffAsync_SetsAdditionsAndDeletions()
    {
        _gitClient.GetChangedFilesAsync(RepoRoot, Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(new List<LocalFileStatus> { new('M', "src/Service.cs") });
        _gitClient.GetFileContentAtRefAsync(RepoRoot, "src/Service.cs", "HEAD", Arg.Any<CancellationToken>())
            .Returns("old line");
        _gitClient.GetFileContentAtRefAsync(RepoRoot, "src/Service.cs", LocalGitClient.WorkingTreeRef, Arg.Any<CancellationToken>())
            .Returns("new line");

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
