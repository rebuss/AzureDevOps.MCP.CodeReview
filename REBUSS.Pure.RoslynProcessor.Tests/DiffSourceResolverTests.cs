using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using REBUSS.Pure.Core;
using REBUSS.Pure.RoslynProcessor;

namespace REBUSS.Pure.RoslynProcessor.Tests;

public class DiffSourceResolverTests : IDisposable
{
    private readonly IRepositoryDownloadOrchestrator _orchestrator = Substitute.For<IRepositoryDownloadOrchestrator>();
    private readonly DiffSourceResolver _resolver;
    private readonly string _tempDir;

    public DiffSourceResolverTests()
    {
        _resolver = new DiffSourceResolver(
            _orchestrator,
            NullLogger<DiffSourceResolver>.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), $"resolver-test-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task ResolveAsync_FileExists_ReturnsPairWithBeforeAndAfter()
    {
        // Simulate ZIP extraction: _tempDir/wrapper/src/File.cs
        // ResolveRoot detects the single wrapper directory and uses it as root
        var wrapperDir = Path.Combine(_tempDir, "repo-abc123");
        var srcDir = Path.Combine(wrapperDir, "src");
        Directory.CreateDirectory(srcDir);

        var afterCode = "class C { void Foo(int x, string y) { } }";
        File.WriteAllText(Path.Combine(srcDir, "File.cs"), afterCode);

        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns(_tempDir);

        var diff = "=== src/File.cs (edit: +1 -1) ===\n@@ -1,1 +1,1 @@\n-class C { void Foo(int x) { } }\n+class C { void Foo(int x, string y) { } }";
        var result = await _resolver.ResolveAsync(diff);

        Assert.NotNull(result);
        Assert.Equal("src/File.cs", result.FilePath);
        Assert.Equal(afterCode, result.AfterCode);
        Assert.Contains("Foo(int x)", result.BeforeCode);
    }

    [Fact]
    public async Task ResolveAsync_RepoNotReady_ReturnsNull()
    {
        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns((string?)null);

        var diff = "=== src/File.cs (edit: +1 -1) ===\n@@ -1,1 +1,1 @@\n-old\n+new";
        var result = await _resolver.ResolveAsync(diff);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_FileNotFound_ReturnsNull()
    {
        Directory.CreateDirectory(_tempDir);
        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns(_tempDir);

        var diff = "=== src/Missing.cs (edit: +1 -1) ===\n@@ -1,1 +1,1 @@\n-old\n+new";
        var result = await _resolver.ResolveAsync(diff);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_FileTooLarge_ReturnsNull()
    {
        Directory.CreateDirectory(_tempDir);
        var srcDir = Path.Combine(_tempDir, "src");
        Directory.CreateDirectory(srcDir);

        // Create a file larger than 100KB
        var largePath = Path.Combine(srcDir, "Large.cs");
        File.WriteAllText(largePath, new string('x', 101 * 1024));

        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns(_tempDir);

        var diff = "=== src/Large.cs (edit: +1 -1) ===\n@@ -1,1 +1,1 @@\n-old\n+new";
        var result = await _resolver.ResolveAsync(diff);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_TimeoutWaitingForRepo_ReturnsNull()
    {
        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync<OperationCanceledException>();

        var diff = "=== src/File.cs (edit: +1 -1) ===\n@@ -1,1 +1,1 @@\n-old\n+new";
        var result = await _resolver.ResolveAsync(diff);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_MultiHunkDiff_ReconstructsBeforeCorrectly()
    {
        var wrapperDir = Path.Combine(_tempDir, "repo-abc123");
        var srcDir = Path.Combine(wrapperDir, "src");
        Directory.CreateDirectory(srcDir);

        var afterCode = "line1\nline2\nNEW\nline4\nline5\nNEW2\nline7";
        File.WriteAllText(Path.Combine(srcDir, "File.cs"), afterCode);
        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns(_tempDir);

        // Multi-hunk diff: two separate changes
        var diff = "=== src/File.cs (edit: +2 -2) ===\n@@ -1,3 +1,3 @@\n line1\n line2\n-OLD\n+NEW\n line4\n@@ -5,3 +5,3 @@\n line5\n-OLD2\n+NEW2\n line7";
        var result = await _resolver.ResolveAsync(diff);

        Assert.NotNull(result);
        // Before code should contain: line1, line2, OLD, line4, line5, OLD2, line7
        Assert.Contains("OLD", result.BeforeCode);
        Assert.Contains("OLD2", result.BeforeCode);
        Assert.DoesNotContain("NEW", result.BeforeCode);
        // Context lines should be in before
        Assert.Contains("line1", result.BeforeCode);
        Assert.Contains("line4", result.BeforeCode);
    }

    [Fact]
    public async Task ResolveAsync_CrlfDiff_NormalizedCorrectly()
    {
        var wrapperDir = Path.Combine(_tempDir, "repo-abc123");
        var srcDir = Path.Combine(wrapperDir, "src");
        Directory.CreateDirectory(srcDir);

        var afterCode = "class C { void Foo() { } }";
        File.WriteAllText(Path.Combine(srcDir, "File.cs"), afterCode);
        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns(_tempDir);

        // CRLF diff
        var diff = "=== src/File.cs (edit: +1 -1) ===\r\n@@ -1,1 +1,1 @@\r\n-class C { void Bar() { } }\r\n+class C { void Foo() { } }";
        var result = await _resolver.ResolveAsync(diff);

        Assert.NotNull(result);
        Assert.Contains("Bar", result.BeforeCode);
    }

    [Fact]
    public async Task ResolveAsync_NoDiffHeader_ReturnsNull()
    {
        var diff = "just some random text without a diff header";
        var result = await _resolver.ResolveAsync(diff);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_SameFilePath_ReturnsCachedResult_NoSecondIo()
    {
        var wrapperDir = Path.Combine(_tempDir, "repo-abc123");
        var srcDir = Path.Combine(wrapperDir, "src");
        Directory.CreateDirectory(srcDir);

        File.WriteAllText(Path.Combine(srcDir, "File.cs"), "class C { }");
        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns(_tempDir);

        var diff = "=== src/File.cs (edit: +1 -1) ===\n@@ -1,1 +1,1 @@\n-old\n+class C { }";

        var result1 = await _resolver.ResolveAsync(diff);
        var result2 = await _resolver.ResolveAsync(diff);

        Assert.NotNull(result1);
        Assert.Same(result1, result2); // Reference equality — cached

        // GetExtractedPathAsync should be called only once (first resolve)
        await _orchestrator.Received(1).GetExtractedPathAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_NullResult_IsCached_NoSecondIo()
    {
        Directory.CreateDirectory(_tempDir);
        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns(_tempDir);

        var diff = "=== src/Missing.cs (edit: +1 -1) ===\n@@ -1,1 +1,1 @@\n-old\n+new";

        var result1 = await _resolver.ResolveAsync(diff);
        var result2 = await _resolver.ResolveAsync(diff);

        Assert.Null(result1);
        Assert.Null(result2);

        // Only one I/O attempt — second call returns cached null
        await _orchestrator.Received(1).GetExtractedPathAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_FirstCallerCancelled_SecondCallerStillGetsResult()
    {
        // The shared Lazy<Task<>> must not inherit the first caller's CancellationToken —
        // otherwise cancelling caller A faults the shared task for caller B with a
        // still-valid token.
        var wrapperDir = Path.Combine(_tempDir, "repo-abc123");
        var srcDir = Path.Combine(wrapperDir, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "File.cs"), "class C { }");

        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns(_ => gate.Task);

        var diff = "=== src/File.cs (edit: +1 -1) ===\n@@ -1,1 +1,1 @@\n-old\n+class C { }";

        using var ctsA = new CancellationTokenSource();
        var taskA = _resolver.ResolveAsync(diff, ctsA.Token);
        var taskB = _resolver.ResolveAsync(diff, CancellationToken.None);

        // Cancel caller A before the orchestrator resolves.
        ctsA.Cancel();

        // Release the orchestrator so the shared task completes successfully.
        gate.SetResult(_tempDir);

        // Caller A observes its own cancellation.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => taskA);

        // Caller B still gets the successful result — not an OCE inherited from A.
        var resultB = await taskB;
        Assert.NotNull(resultB);
        Assert.Equal("src/File.cs", resultB.FilePath);
    }

    [Fact]
    public async Task ResolveAsync_DifferentFilePaths_ResolveSeparately()
    {
        var wrapperDir = Path.Combine(_tempDir, "repo-abc123");
        var srcDir = Path.Combine(wrapperDir, "src");
        Directory.CreateDirectory(srcDir);

        File.WriteAllText(Path.Combine(srcDir, "A.cs"), "class A { }");
        File.WriteAllText(Path.Combine(srcDir, "B.cs"), "class B { }");
        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns(_tempDir);

        var diffA = "=== src/A.cs (edit: +1 -1) ===\n@@ -1,1 +1,1 @@\n-old\n+class A { }";
        var diffB = "=== src/B.cs (edit: +1 -1) ===\n@@ -1,1 +1,1 @@\n-old\n+class B { }";

        var resultA = await _resolver.ResolveAsync(diffA);
        var resultB = await _resolver.ResolveAsync(diffB);

        Assert.NotNull(resultA);
        Assert.NotNull(resultB);
        Assert.NotSame(resultA, resultB);
        Assert.Equal("src/A.cs", resultA.FilePath);
        Assert.Equal("src/B.cs", resultB.FilePath);

        // Both paths require their own resolution
        await _orchestrator.Received(2).GetExtractedPathAsync(Arg.Any<CancellationToken>());
    }
}
