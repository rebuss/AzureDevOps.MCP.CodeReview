using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using REBUSS.Pure.Core;
using REBUSS.Pure.RoslynProcessor;

namespace REBUSS.Pure.RoslynProcessor.Tests;

public class ScopeAnnotatorEnricherTests : IDisposable
{
    private readonly IRepositoryDownloadOrchestrator _orchestrator = Substitute.For<IRepositoryDownloadOrchestrator>();
    private readonly DiffSourceResolver _sourceResolver;
    private readonly ScopeAnnotatorEnricher _enricher;
    private readonly string _tempDir;

    public ScopeAnnotatorEnricherTests()
    {
        _sourceResolver = new DiffSourceResolver(
            _orchestrator,
            NullLogger<DiffSourceResolver>.Instance);
        _enricher = new ScopeAnnotatorEnricher(
            _sourceResolver,
            NullLogger<ScopeAnnotatorEnricher>.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), $"scope-test-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void CanEnrich_CsFile_ReturnsTrue()
    {
        Assert.True(_enricher.CanEnrich("=== src/File.cs (edit: +1 -1) ===\n@@"));
    }

    [Fact]
    public void CanEnrich_PyFile_ReturnsFalse()
    {
        Assert.False(_enricher.CanEnrich("=== src/main.py (edit: +1 -1) ===\n@@"));
    }

    [Fact]
    public void CanEnrich_SkippedFile_ReturnsFalse()
    {
        Assert.False(_enricher.CanEnrich("=== src/File.cs (edit: skipped) ===\n"));
    }

    [Fact]
    public async Task EnrichAsync_SingleHunk_AnnotatesWithScope()
    {
        var wrapperDir = Path.Combine(_tempDir, "repo");
        var srcDir = Path.Combine(wrapperDir, "src");
        Directory.CreateDirectory(srcDir);

        var afterCode = @"class OrderService
{
    void ProcessOrder(Order o)
    {
        var x = 1;
    }
}";
        File.WriteAllText(Path.Combine(srcDir, "OrderService.cs"), afterCode);
        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns(_tempDir);

        var diff = "=== src/OrderService.cs (edit: +1 -1) ===\n@@ -4,1 +4,1 @@\n-        var x = 0;\n+        var x = 1;";
        var result = await _enricher.EnrichAsync(diff);

        Assert.Contains("[scope:", result);
        Assert.Contains("OrderService", result);
        Assert.Contains("ProcessOrder", result);
    }

    [Fact]
    public async Task EnrichAsync_MultipleHunks_EachAnnotatedSeparately()
    {
        var wrapperDir = Path.Combine(_tempDir, "repo");
        var srcDir = Path.Combine(wrapperDir, "src");
        Directory.CreateDirectory(srcDir);

        var afterCode = @"class Svc
{
    void MethodA()
    {
        var a = 1;
    }
    void MethodB()
    {
        var b = 2;
    }
}";
        File.WriteAllText(Path.Combine(srcDir, "Svc.cs"), afterCode);
        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns(_tempDir);

        var diff = "=== src/Svc.cs (edit: +2 -2) ===\n@@ -5,1 +5,1 @@\n-        var a = 0;\n+        var a = 1;\n@@ -9,1 +9,1 @@\n-        var b = 0;\n+        var b = 2;";
        var result = await _enricher.EnrichAsync(diff);

        Assert.Contains("MethodA", result);
        Assert.Contains("MethodB", result);
    }

    [Fact]
    public async Task EnrichAsync_HunkAtClassLevel_AnnotatesWithClassName()
    {
        var wrapperDir = Path.Combine(_tempDir, "repo");
        var srcDir = Path.Combine(wrapperDir, "src");
        Directory.CreateDirectory(srcDir);

        var afterCode = @"class OrderService
{
    private readonly int _count = 5;
}";
        File.WriteAllText(Path.Combine(srcDir, "OrderService.cs"), afterCode);
        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns(_tempDir);

        var diff = "=== src/OrderService.cs (edit: +1 -1) ===\n@@ -3,1 +3,1 @@\n-    private readonly int _count = 0;\n+    private readonly int _count = 5;";
        var result = await _enricher.EnrichAsync(diff);

        Assert.Contains("[scope: OrderService]", result);
    }

    [Fact]
    public async Task EnrichAsync_ScopeNotResolved_HunkUnchanged()
    {
        var wrapperDir = Path.Combine(_tempDir, "repo");
        var srcDir = Path.Combine(wrapperDir, "src");
        Directory.CreateDirectory(srcDir);

        File.WriteAllText(Path.Combine(srcDir, "File.cs"), "// empty");
        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns(_tempDir);

        var diff = "=== src/File.cs (edit: +1 -0) ===\n@@ -1,0 +1,1 @@\n+// new line";
        var result = await _enricher.EnrichAsync(diff);

        // Scope resolver may or may not find a scope for line 1 in a comment-only file
        // Either way, the diff should not be corrupted
        Assert.Contains("@@ -1,0 +1,1 @@", result);
    }

    // ─── Feature 011 — scope determinism + file-level handling (FR-005, FR-010) ───

    [Fact]
    public async Task EnrichAsync_HunkInUsingDirectives_DoesNotMisattributeToMethod()
    {
        // A hunk that touches only using-directives at the top of a file must not
        // be misattributed to whatever method happens to live below them.
        var srcDir = Path.Combine(_tempDir, "repo", "src");
        Directory.CreateDirectory(srcDir);

        var afterCode = @"using System;
using System.Linq;
using System.Text.Json;

class Svc
{
    void DoWork()
    {
        var x = 1;
    }
}";
        File.WriteAllText(Path.Combine(srcDir, "Svc.cs"), afterCode);
        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns(_tempDir);

        var diff = "=== src/Svc.cs (edit: +1 -0) ===\n@@ -2,0 +3,1 @@\n+using System.Text.Json;";
        var result = await _enricher.EnrichAsync(diff);

        // The annotation must NOT name `DoWork` — that method is not at the hunk site.
        Assert.DoesNotContain("DoWork", result);
    }

    [Fact]
    public async Task EnrichAsync_SameInputTwice_ProducesIdenticalOutput()
    {
        // FR-010: scope attribution must be deterministic — repeated runs over the
        // same input must produce the same annotation.
        var srcDir = Path.Combine(_tempDir, "repo", "src");
        Directory.CreateDirectory(srcDir);

        var afterCode = @"class Svc
{
    void Process()
    {
        var x = 42;
    }
}";
        File.WriteAllText(Path.Combine(srcDir, "Svc.cs"), afterCode);
        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns(_tempDir);

        var diff = "=== src/Svc.cs (edit: +1 -1) ===\n@@ -4,1 +4,1 @@\n-        var x = 0;\n+        var x = 42;";

        var first = await _enricher.EnrichAsync(diff);
        var second = await _enricher.EnrichAsync(diff);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task EnrichAsync_RepoNotReady_ReturnsDiffUnchanged()
    {
        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns((string?)null);

        var diff = "=== src/File.cs (edit: +1 -1) ===\n@@ -1,1 +1,1 @@\n-old\n+new";
        var result = await _enricher.EnrichAsync(diff);

        Assert.Equal(diff, result);
    }

    [Fact]
    public async Task EnrichAsync_AfterBeforeAfterEnricher_ParsesModifiedHunks()
    {
        // Simulates a diff that was already modified by BeforeAfterEnricher
        // (different hunk headers than original)
        var wrapperDir = Path.Combine(_tempDir, "repo");
        var srcDir = Path.Combine(wrapperDir, "src");
        Directory.CreateDirectory(srcDir);

        var afterCode = @"class Svc
{
    void Run()
    {
        var x = 1;
    }
}";
        File.WriteAllText(Path.Combine(srcDir, "Svc.cs"), afterCode);
        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns(_tempDir);

        // Diff with expanded context (as if BeforeAfterEnricher already ran)
        var diff = "=== src/Svc.cs (edit: +1 -1) ===\n@@ -3,5 +3,5 @@\n     void Run()\n     {\n-        var x = 0;\n+        var x = 1;\n     }";
        var result = await _enricher.EnrichAsync(diff);

        Assert.Contains("[scope:", result);
    }
}
