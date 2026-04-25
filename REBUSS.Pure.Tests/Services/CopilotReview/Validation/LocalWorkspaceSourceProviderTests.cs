using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using REBUSS.Pure.Core;
using REBUSS.Pure.Services.CopilotReview.Validation;
using REBUSS.Pure.Services.LocalReview;

namespace REBUSS.Pure.Tests.Services.CopilotReview.Validation;

/// <summary>Unit tests for <see cref="LocalWorkspaceSourceProvider"/>. Feature 023.</summary>
public class LocalWorkspaceSourceProviderTests
{
    private readonly IWorkspaceRootProvider _workspaceRootProvider = Substitute.For<IWorkspaceRootProvider>();
    private readonly ILocalGitClient _localGitClient = Substitute.For<ILocalGitClient>();
    private readonly LocalWorkspaceSourceProvider _provider;

    public LocalWorkspaceSourceProviderTests()
    {
        _provider = new LocalWorkspaceSourceProvider(
            _workspaceRootProvider,
            _localGitClient,
            NullLogger<LocalWorkspaceSourceProvider>.Instance);
    }

    [Fact]
    public async Task GetAfterCodeAsync_IndexRef_HappyPath_ReturnsContent()
    {
        // Spec FR-001a — local:staged reads from the git index via IndexRef.
        _workspaceRootProvider.ResolveRepositoryRoot().Returns("C:/Repo");
        _localGitClient.GetFileContentAtRefAsync(
                "C:/Repo", "src/Foo.cs", LocalGitClient.IndexRef, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("class Foo {}"));

        var result = await _provider.GetAfterCodeAsync(
            "src/Foo.cs", LocalGitClient.IndexRef, NoOp, CancellationToken.None);

        Assert.Equal("class Foo {}", result);
    }

    [Fact]
    public async Task GetAfterCodeAsync_WorkingTreeRef_HappyPath_ReturnsContent()
    {
        // Spec FR-001a — local:unstaged reads from working tree via WorkingTreeRef.
        _workspaceRootProvider.ResolveRepositoryRoot().Returns("C:/Repo");
        _localGitClient.GetFileContentAtRefAsync(
                "C:/Repo", "src/Foo.cs", LocalGitClient.WorkingTreeRef, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("class FooEdited {}"));

        var result = await _provider.GetAfterCodeAsync(
            "src/Foo.cs", LocalGitClient.WorkingTreeRef, NoOp, CancellationToken.None);

        Assert.Equal("class FooEdited {}", result);
    }

    [Fact]
    public async Task GetAfterCodeAsync_HeadRef_HappyPath_ReturnsContent()
    {
        // Spec FR-001a — local:branch reads from current branch HEAD via "HEAD".
        _workspaceRootProvider.ResolveRepositoryRoot().Returns("C:/Repo");
        _localGitClient.GetFileContentAtRefAsync(
                "C:/Repo", "src/Foo.cs", "HEAD", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("class FooHead {}"));

        var result = await _provider.GetAfterCodeAsync(
            "src/Foo.cs", "HEAD", NoOp, CancellationToken.None);

        Assert.Equal("class FooHead {}", result);
    }

    [Fact]
    public async Task GetAfterCodeAsync_WorkspaceRootNull_InvokesCallbackAndReturnsNull()
    {
        // Spec FR-006 — workspace-root-missing must surface via the supplied callback so
        // the wrapper can dedupe to one warning per review.
        _workspaceRootProvider.ResolveRepositoryRoot().Returns((string?)null);

        var callbackCount = 0;
        var result = await _provider.GetAfterCodeAsync(
            "src/Foo.cs", LocalGitClient.IndexRef, () => callbackCount++, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, callbackCount);
        // Provider must NOT be called when there's no root.
        await _localGitClient.DidNotReceiveWithAnyArgs().GetFileContentAtRefAsync(
            default!, default!, default!, default);
    }

    [Fact]
    public async Task GetAfterCodeAsync_WorkspaceRootEmpty_InvokesCallbackAndReturnsNull()
    {
        _workspaceRootProvider.ResolveRepositoryRoot().Returns("");

        var callbackCount = 0;
        var result = await _provider.GetAfterCodeAsync(
            "src/Foo.cs", LocalGitClient.IndexRef, () => callbackCount++, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, callbackCount);
    }

    [Fact]
    public async Task GetAfterCodeAsync_FileNotPresentAtRef_ReturnsNull()
    {
        // Spec FR-004 — file deleted / not in index → SourceUnavailable (null).
        _workspaceRootProvider.ResolveRepositoryRoot().Returns("C:/Repo");
        _localGitClient.GetFileContentAtRefAsync(
                "C:/Repo", "src/Missing.cs", LocalGitClient.IndexRef, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var result = await _provider.GetAfterCodeAsync(
            "src/Missing.cs", LocalGitClient.IndexRef, NoOp, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAfterCodeAsync_ContentExceedsSizeLimit_ReturnsNull()
    {
        // Spec FR-005 — 100 KB cap parity with PR mode.
        _workspaceRootProvider.ResolveRepositoryRoot().Returns("C:/Repo");
        var oversize = new string('x', 100 * 1024 + 1);
        _localGitClient.GetFileContentAtRefAsync(
                "C:/Repo", "src/Big.cs", LocalGitClient.IndexRef, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(oversize));

        var result = await _provider.GetAfterCodeAsync(
            "src/Big.cs", LocalGitClient.IndexRef, NoOp, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAfterCodeAsync_Cancellation_Propagates()
    {
        // Spec FR-010 — OperationCanceledException MUST propagate.
        _workspaceRootProvider.ResolveRepositoryRoot().Returns("C:/Repo");
        _localGitClient.GetFileContentAtRefAsync(
                "C:/Repo", "src/Foo.cs", LocalGitClient.IndexRef, Arg.Any<CancellationToken>())
            .Returns<Task<string?>>(_ => Task.FromException<string?>(new OperationCanceledException()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _provider.GetAfterCodeAsync(
                "src/Foo.cs", LocalGitClient.IndexRef, NoOp, CancellationToken.None));
    }

    [Fact]
    public async Task GetAfterCodeAsync_GitClientGenericException_LoggedAndReturnsNull()
    {
        // Generic git failure — log Debug and return null (don't throw).
        _workspaceRootProvider.ResolveRepositoryRoot().Returns("C:/Repo");
        _localGitClient.GetFileContentAtRefAsync(
                "C:/Repo", "src/Foo.cs", LocalGitClient.IndexRef, Arg.Any<CancellationToken>())
            .Returns<Task<string?>>(_ => Task.FromException<string?>(new InvalidOperationException("git crashed")));

        var result = await _provider.GetAfterCodeAsync(
            "src/Foo.cs", LocalGitClient.IndexRef, NoOp, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAfterCodeAsync_ResolvesWorkspaceRootPerCall_NotCached()
    {
        // Constitution §V — workspace root must be resolved per call, never cached in ctor.
        _workspaceRootProvider.ResolveRepositoryRoot().Returns("C:/Repo");
        _localGitClient.GetFileContentAtRefAsync(
                "C:/Repo", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("ok"));

        await _provider.GetAfterCodeAsync("a.cs", LocalGitClient.IndexRef, NoOp, CancellationToken.None);
        await _provider.GetAfterCodeAsync("b.cs", LocalGitClient.IndexRef, NoOp, CancellationToken.None);
        await _provider.GetAfterCodeAsync("c.cs", LocalGitClient.IndexRef, NoOp, CancellationToken.None);

        _workspaceRootProvider.Received(3).ResolveRepositoryRoot();
    }

    private static void NoOp() { }
}
