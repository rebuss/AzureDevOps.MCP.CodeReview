using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using REBUSS.Pure.Core;
using REBUSS.Pure.Services.CopilotReview.Validation;
using REBUSS.Pure.Services.LocalReview;

namespace REBUSS.Pure.Tests.Services.CopilotReview.Validation;

/// <summary>Unit tests for <see cref="FindingSourceProviderSelector"/>. Feature 023.</summary>
public class FindingSourceProviderSelectorTests
{
    private readonly IRepositoryDownloadOrchestrator _orchestrator = Substitute.For<IRepositoryDownloadOrchestrator>();
    private readonly IWorkspaceRootProvider _workspaceRootProvider = Substitute.For<IWorkspaceRootProvider>();
    private readonly ILocalGitClient _localGitClient = Substitute.For<ILocalGitClient>();
    private readonly RemoteArchiveSourceProvider _remote;
    private readonly LocalWorkspaceSourceProvider _local;
    private readonly FindingSourceProviderSelector _selector;

    public FindingSourceProviderSelectorTests()
    {
        _remote = new RemoteArchiveSourceProvider(_orchestrator, NullLogger<RemoteArchiveSourceProvider>.Instance);
        _local = new LocalWorkspaceSourceProvider(
            _workspaceRootProvider, _localGitClient, NullLogger<LocalWorkspaceSourceProvider>.Instance);
        _selector = new FindingSourceProviderSelector(
            _remote, _local, NullLogger<FindingSourceProviderSelector>.Instance);
    }

    [Fact]
    public async Task SelectFor_LocalStaged_BindsIndexRef()
    {
        _workspaceRootProvider.ResolveRepositoryRoot().Returns("C:/Repo");
        _localGitClient.GetFileContentAtRefAsync(
                "C:/Repo", "src/Foo.cs", LocalGitClient.IndexRef, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("staged-content"));

        var provider = _selector.SelectFor("local:staged:C:\\Repo");
        var result = await provider.GetAfterCodeAsync("src/Foo.cs", CancellationToken.None);

        Assert.Equal("staged-content", result);
        await _localGitClient.Received(1).GetFileContentAtRefAsync(
            "C:/Repo", "src/Foo.cs", LocalGitClient.IndexRef, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SelectFor_LocalUnstaged_BindsWorkingTreeRef()
    {
        _workspaceRootProvider.ResolveRepositoryRoot().Returns("C:/Repo");
        _localGitClient.GetFileContentAtRefAsync(
                "C:/Repo", "src/Foo.cs", LocalGitClient.WorkingTreeRef, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("working-tree-content"));

        var provider = _selector.SelectFor("local:unstaged:C:\\Repo");
        var result = await provider.GetAfterCodeAsync("src/Foo.cs", CancellationToken.None);

        Assert.Equal("working-tree-content", result);
        await _localGitClient.Received(1).GetFileContentAtRefAsync(
            "C:/Repo", "src/Foo.cs", LocalGitClient.WorkingTreeRef, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SelectFor_LocalBranch_BindsHeadRef()
    {
        _workspaceRootProvider.ResolveRepositoryRoot().Returns("C:/Repo");
        _localGitClient.GetFileContentAtRefAsync(
                "C:/Repo", "src/Foo.cs", "HEAD", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("head-content"));

        var provider = _selector.SelectFor("local:branch:main:C:\\Repo");
        var result = await provider.GetAfterCodeAsync("src/Foo.cs", CancellationToken.None);

        Assert.Equal("head-content", result);
        await _localGitClient.Received(1).GetFileContentAtRefAsync(
            "C:/Repo", "src/Foo.cs", "HEAD", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("azure-devops:org/proj/PR42")]
    [InlineData("github:owner/repo/PR1")]
    [InlineData("pr:42")]
    [InlineData("")]                       // empty key → not local prefix → remote
    [InlineData("local:")]                 // bare "local:" without recognized sub-prefix
    [InlineData("LOCAL:STAGED:foo")]       // case-sensitive Ordinal → not matched as local
    public void SelectFor_NonLocalPrefix_ReturnsRemoteProvider(string reviewKey)
    {
        var provider = _selector.SelectFor(reviewKey);
        Assert.Same(_remote, provider);
    }

    [Fact]
    public async Task BoundLocalProvider_DedupsWorkspaceRootMissingWarning_PerWrapperInstance()
    {
        // Spec FR-006 — at most one warning per review when workspace root unresolvable.
        // The wrapper holds per-review dedup state; multiple calls within the same
        // SelectFor result must produce one warning.
        _workspaceRootProvider.ResolveRepositoryRoot().Returns((string?)null);

        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<FindingSourceProviderSelector>>();
        logger.IsEnabled(Arg.Any<Microsoft.Extensions.Logging.LogLevel>()).Returns(true);
        var selector = new FindingSourceProviderSelector(_remote, _local, logger);

        var provider = selector.SelectFor("local:staged:C:\\Repo");

        // Three findings within the same review — one warning expected.
        await provider.GetAfterCodeAsync("a.cs", CancellationToken.None);
        await provider.GetAfterCodeAsync("b.cs", CancellationToken.None);
        await provider.GetAfterCodeAsync("c.cs", CancellationToken.None);

        // One Warning-level log in total across the three calls.
        logger.Received(1).Log(
            Microsoft.Extensions.Logging.LogLevel.Warning,
            Arg.Any<Microsoft.Extensions.Logging.EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task BoundLocalProvider_FreshDedupState_PerSelectForCall()
    {
        // Two distinct SelectFor calls (== two distinct reviews) each carry their own
        // warning state. Second wrapper still warns once even though the first already did.
        _workspaceRootProvider.ResolveRepositoryRoot().Returns((string?)null);

        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<FindingSourceProviderSelector>>();
        logger.IsEnabled(Arg.Any<Microsoft.Extensions.Logging.LogLevel>()).Returns(true);
        var selector = new FindingSourceProviderSelector(_remote, _local, logger);

        var p1 = selector.SelectFor("local:staged:C:\\Repo1");
        await p1.GetAfterCodeAsync("a.cs", CancellationToken.None);
        await p1.GetAfterCodeAsync("b.cs", CancellationToken.None);

        var p2 = selector.SelectFor("local:staged:C:\\Repo2");
        await p2.GetAfterCodeAsync("c.cs", CancellationToken.None);
        await p2.GetAfterCodeAsync("d.cs", CancellationToken.None);

        // Two warnings total — one per wrapper / per review.
        logger.Received(2).Log(
            Microsoft.Extensions.Logging.LogLevel.Warning,
            Arg.Any<Microsoft.Extensions.Logging.EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}
