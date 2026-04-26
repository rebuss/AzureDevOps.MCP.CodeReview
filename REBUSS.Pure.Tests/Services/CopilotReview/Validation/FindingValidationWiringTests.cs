using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using REBUSS.Pure.Core;
using REBUSS.Pure.RoslynProcessor;
using REBUSS.Pure.Services.CopilotReview.Validation;
using REBUSS.Pure.Services.LocalReview;

namespace REBUSS.Pure.Tests.Services.CopilotReview.Validation;

/// <summary>
/// Integration-style wiring tests for the Feature 023 review-mode-aware source pipeline.
/// Uses real <see cref="FindingScopeResolver"/> + <see cref="FindingSourceProviderSelector"/>
/// + real provider implementations; only the external dependencies
/// (<see cref="IRepositoryDownloadOrchestrator"/>, <see cref="IWorkspaceRootProvider"/>,
/// <see cref="ILocalGitClient"/>) are mocked. These tests verify each user-story review
/// mode (US1 local:staged, US2 local:working-tree + local:branch-diff, US3 PR) reaches the
/// correct underlying source primitive — covering the orchestrator's one-line change
/// (forwarding job.ReviewKey to FindingScopeResolver.ResolveAsync) without standing up
/// the full validator/agent stack.
/// </summary>
public class FindingValidationWiringTests : IDisposable
{
    private readonly IRepositoryDownloadOrchestrator _orchestrator =
        Substitute.For<IRepositoryDownloadOrchestrator>();
    private readonly IWorkspaceRootProvider _workspaceRootProvider =
        Substitute.For<IWorkspaceRootProvider>();
    private readonly ILocalGitClient _localGitClient =
        Substitute.For<ILocalGitClient>();

    private readonly FindingScopeResolver _resolver;
    private readonly string _tempDir;

    public FindingValidationWiringTests()
    {
        var remote = new RemoteArchiveSourceProvider(
            _orchestrator, NullLogger<RemoteArchiveSourceProvider>.Instance);
        var local = new LocalWorkspaceSourceProvider(
            _workspaceRootProvider, _localGitClient, NullLogger<LocalWorkspaceSourceProvider>.Instance);
        var selector = new FindingSourceProviderSelector(
            remote, local, NullLogger<FindingSourceProviderSelector>.Instance);
        _resolver = new FindingScopeResolver(selector, NullLogger<FindingScopeResolver>.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), $"finding-wiring-test-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    private static ParsedFinding MakeFinding(string filePath, int line) => new()
    {
        Index = 0,
        FilePath = filePath,
        LineNumber = line,
        Severity = "major",
        Description = "test",
        OriginalText = "test",
    };

    /// <summary>
    /// US1 — local:staged review. The validator must read from the git index so its
    /// verdicts judge the exact bytes the staged diff represented (not the working tree
    /// the developer may have continued editing).
    /// </summary>
    [Fact]
    public async Task ResolveAsync_LocalStagedReview_ReadsFromGitIndex()
    {
        _workspaceRootProvider.ResolveRepositoryRoot().Returns("C:/Repo");
        _localGitClient.GetFileContentAtRefAsync(
                "C:/Repo", "src/Foo.cs", LocalGitClient.IndexRef, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("""
                namespace Test;
                public class Foo
                {
                    public void Bar() { var x = 1; }
                }
                """));

        var findings = new[] { MakeFinding("src/Foo.cs", line: 4) };

        var result = await _resolver.ResolveAsync(
            findings, "local:staged:C:\\Repo", maxScopeLines: 150, CancellationToken.None);

        Assert.Equal(ScopeResolutionFailure.None, Assert.Single(result).ResolutionFailure);
        await _localGitClient.Received(1).GetFileContentAtRefAsync(
            "C:/Repo", "src/Foo.cs", LocalGitClient.IndexRef, Arg.Any<CancellationToken>());
        // Sanity: the PR-mode path must NOT have been called for a local review.
        await _orchestrator.DidNotReceiveWithAnyArgs().GetExtractedPathAsync(default);
    }

    /// <summary>
    /// US2 — local:working-tree review reads the working tree (the diff's after-side for
    /// uncommitted edits). Key form mirrors <c>LocalReviewScope.WorkingTree().ToString()</c>.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_LocalWorkingTreeReview_ReadsFromWorkingTree()
    {
        _workspaceRootProvider.ResolveRepositoryRoot().Returns("C:/Repo");
        _localGitClient.GetFileContentAtRefAsync(
                "C:/Repo", "src/Foo.cs", LocalGitClient.WorkingTreeRef, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("""
                namespace Test;
                public class Foo { public void Bar() { } }
                """));

        var findings = new[] { MakeFinding("src/Foo.cs", line: 2) };

        var result = await _resolver.ResolveAsync(
            findings, "local:working-tree:C:\\Repo", maxScopeLines: 150, CancellationToken.None);

        Assert.Equal(ScopeResolutionFailure.None, Assert.Single(result).ResolutionFailure);
        await _localGitClient.Received(1).GetFileContentAtRefAsync(
            "C:/Repo", "src/Foo.cs", LocalGitClient.WorkingTreeRef, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// US2 — local:branch-diff review reads the current branch HEAD (the diff's after-side
    /// for &lt;base&gt;...HEAD). Key form mirrors <c>LocalReviewScope.BranchDiff(base).ToString()</c>
    /// (i.e. <c>"branch-diff:&lt;base&gt;"</c>).
    /// </summary>
    [Fact]
    public async Task ResolveAsync_LocalBranchDiffReview_ReadsFromBranchHead()
    {
        _workspaceRootProvider.ResolveRepositoryRoot().Returns("C:/Repo");
        _localGitClient.GetFileContentAtRefAsync(
                "C:/Repo", "src/Foo.cs", "HEAD", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("""
                namespace Test;
                public class Foo { public void Bar() { } }
                """));

        var findings = new[] { MakeFinding("src/Foo.cs", line: 2) };

        var result = await _resolver.ResolveAsync(
            findings, "local:branch-diff:main:C:\\Repo", maxScopeLines: 150, CancellationToken.None);

        Assert.Equal(ScopeResolutionFailure.None, Assert.Single(result).ResolutionFailure);
        await _localGitClient.Received(1).GetFileContentAtRefAsync(
            "C:/Repo", "src/Foo.cs", "HEAD", Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// US3 — PR review must continue to use the archive-download path. No regression:
    /// no local git invocation is made for PR-style review keys.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_PrReview_ReadsFromExtractedArchive_NoLocalGitInvocation()
    {
        var inner = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(Path.Combine(inner, "src"));
        await File.WriteAllTextAsync(
            Path.Combine(inner, "src", "Foo.cs"),
            """
            namespace Test;
            public class Foo { public void Bar() { } }
            """);
        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns(_tempDir);

        var findings = new[] { MakeFinding("src/Foo.cs", line: 2) };

        var result = await _resolver.ResolveAsync(
            findings, "azure-devops:org/proj/PR42", maxScopeLines: 150, CancellationToken.None);

        Assert.Equal(ScopeResolutionFailure.None, Assert.Single(result).ResolutionFailure);
        await _orchestrator.Received().GetExtractedPathAsync(Arg.Any<CancellationToken>());
        // No local git path touched for a PR review.
        await _localGitClient.DidNotReceiveWithAnyArgs().GetFileContentAtRefAsync(
            default!, default!, default!, default);
    }

    /// <summary>
    /// US1 edge case — file deleted from the index produces SourceUnavailable verdict
    /// gracefully (FR-004).
    /// </summary>
    [Fact]
    public async Task ResolveAsync_LocalStagedReview_FileNotInIndex_ReturnsSourceUnavailable()
    {
        _workspaceRootProvider.ResolveRepositoryRoot().Returns("C:/Repo");
        _localGitClient.GetFileContentAtRefAsync(
                "C:/Repo", "src/Deleted.cs", LocalGitClient.IndexRef, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var findings = new[] { MakeFinding("src/Deleted.cs", line: 1) };

        var result = await _resolver.ResolveAsync(
            findings, "local:staged:C:\\Repo", maxScopeLines: 150, CancellationToken.None);

        Assert.Equal(ScopeResolutionFailure.SourceUnavailable, Assert.Single(result).ResolutionFailure);
    }
}
