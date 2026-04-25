using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using REBUSS.Pure.Core;
using REBUSS.Pure.Services.CopilotReview.Validation;

namespace REBUSS.Pure.Tests.Services.CopilotReview.Validation;

/// <summary>Unit tests for <see cref="RemoteArchiveSourceProvider"/>. Feature 023.</summary>
public class RemoteArchiveSourceProviderTests : IDisposable
{
    private readonly IRepositoryDownloadOrchestrator _orchestrator = Substitute.For<IRepositoryDownloadOrchestrator>();
    private readonly RemoteArchiveSourceProvider _provider;
    private readonly string _tempDir;

    public RemoteArchiveSourceProviderTests()
    {
        _provider = new RemoteArchiveSourceProvider(_orchestrator, NullLogger<RemoteArchiveSourceProvider>.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), $"remote-archive-source-test-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task GetAfterCodeAsync_HappyPath_ReturnsFileContent()
    {
        // Archive layout: tempDir/repo/src/Foo.cs (single inner dir → ResolveRoot picks it).
        var inner = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(Path.Combine(inner, "src"));
        var filePath = "src/Foo.cs";
        var fullPath = Path.Combine(inner, "src", "Foo.cs");
        await File.WriteAllTextAsync(fullPath, "class Foo {}");
        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns(_tempDir);

        var result = await _provider.GetAfterCodeAsync(filePath, CancellationToken.None);

        Assert.Equal("class Foo {}", result);
    }

    [Fact]
    public async Task GetAfterCodeAsync_ExtractedPathNull_ReturnsNull()
    {
        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await _provider.GetAfterCodeAsync("src/Foo.cs", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAfterCodeAsync_FileNotInArchive_ReturnsNull()
    {
        var inner = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(inner);
        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns(_tempDir);

        var result = await _provider.GetAfterCodeAsync("src/Missing.cs", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAfterCodeAsync_FileExceedsSizeLimit_ReturnsNull()
    {
        // Spec FR-005 — 100 KB cap parity with local mode.
        var inner = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(Path.Combine(inner, "src"));
        var fullPath = Path.Combine(inner, "src", "Big.cs");
        var oversize = new string('x', 100 * 1024 + 1);
        await File.WriteAllTextAsync(fullPath, oversize);
        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns(_tempDir);

        var result = await _provider.GetAfterCodeAsync("src/Big.cs", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAfterCodeAsync_CallerCancellation_Propagates()
    {
        // Spec FR-010 — cancellation MUST propagate, not be swallowed.
        var inner = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(Path.Combine(inner, "src"));
        await File.WriteAllTextAsync(Path.Combine(inner, "src", "Foo.cs"), "class Foo {}");
        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns(_tempDir);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _provider.GetAfterCodeAsync("src/Foo.cs", cts.Token));
    }

    [Fact]
    public async Task GetAfterCodeAsync_OrchestratorTimeout_ReturnsNull_WithoutPropagating()
    {
        // When the 3-minute internal timeout fires (orchestrator throws OCE on timeoutCts
        // but caller's ct is still active), the provider must swallow and return null —
        // matching DiffSourceResolver's contract for FR-003 PR-mode parity.
        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>())
            .Returns<Task<string?>>(_ => Task.FromException<string?>(new OperationCanceledException()));

        var result = await _provider.GetAfterCodeAsync("src/Foo.cs", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAfterCodeAsync_OrchestratorThrowsGenericException_BubblesUp()
    {
        // Generic orchestrator failures (not OCE) propagate so the resolver can tag the
        // file as SourceUnavailable with a Warning log (existing FindingScopeResolver behavior).
        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>())
            .Returns<Task<string?>>(_ => Task.FromException<string?>(new InvalidOperationException("boom")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _provider.GetAfterCodeAsync("src/Foo.cs", CancellationToken.None));
    }
}
