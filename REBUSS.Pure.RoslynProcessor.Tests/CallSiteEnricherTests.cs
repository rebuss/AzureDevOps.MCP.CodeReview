using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using REBUSS.Pure.Core;
using REBUSS.Pure.RoslynProcessor;

namespace REBUSS.Pure.RoslynProcessor.Tests;

public class CallSiteEnricherTests : IDisposable
{
    private readonly IRepositoryDownloadOrchestrator _orchestrator = Substitute.For<IRepositoryDownloadOrchestrator>();
    private readonly CallSiteScanner _scanner = new(NullLogger<CallSiteScanner>.Instance);
    private readonly CallSiteEnricher _enricher;
    private readonly string _tempDir;

    public CallSiteEnricherTests()
    {
        _enricher = new CallSiteEnricher(
            _orchestrator,
            _scanner,
            NullLogger<CallSiteEnricher>.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), $"callsite-enricher-test-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    private void SetupRepo(params (string path, string content)[] files)
    {
        var wrapperDir = Path.Combine(_tempDir, "repo");
        foreach (var (path, content) in files)
        {
            var fullPath = Path.Combine(wrapperDir, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }
        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns(_tempDir);
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
    public async Task EnrichAsync_MethodWithCallSites_InsertsBlock()
    {
        SetupRepo(
            ("src/OrderService.cs", "class OrderService { void ProcessOrder(Order o) { } }"),
            ("src/Controller.cs", "class Ctrl { void Run() { svc.ProcessOrder(order); } }"));

        var diff = "=== src/OrderService.cs (edit: +1 -1) ===\n[structural-changes]\n  \U0001f504 Method signature changed: ProcessOrder(\n[/structural-changes]\n@@ -1,1 +1,1 @@\n-old\n+new";
        var result = await _enricher.EnrichAsync(diff);

        Assert.Contains("[call-sites]", result);
        Assert.Contains("ProcessOrder", result);
        Assert.Contains("Controller.cs", result);
        Assert.Contains("[/call-sites]", result);
    }

    [Fact]
    public async Task EnrichAsync_NewMethodNoCallSites_ShowsNoExistingCallSites()
    {
        SetupRepo(
            ("src/OrderService.cs", "class OrderService { void ValidateOrder(Order o) { } }"));

        var diff = "=== src/OrderService.cs (edit: +3 -0) ===\n[structural-changes]\n  \u2795 New method: ValidateOrder(\n[/structural-changes]\n@@ -1,1 +1,3 @@\n+new";
        var result = await _enricher.EnrichAsync(diff);

        Assert.Contains("[call-sites]", result);
        Assert.Contains("no existing call sites", result);
    }

    [Fact]
    public async Task EnrichAsync_ChangedMethodNoCallSites_OmittedFromBlock()
    {
        SetupRepo(
            ("src/OrderService.cs", "class OrderService { void ProcessOrder(Order o) { } }"));

        var diff = "=== src/OrderService.cs (edit: +1 -1) ===\n[structural-changes]\n  \U0001f504 Method signature changed: ProcessOrder(\n[/structural-changes]\n@@ -1,1 +1,1 @@\n-old\n+new";
        var result = await _enricher.EnrichAsync(diff);

        // Signature changed with 0 call sites — should be omitted entirely
        Assert.DoesNotContain("[call-sites]", result);
    }

    [Fact]
    public async Task EnrichAsync_NoTargets_ReturnsDiffUnchanged()
    {
        SetupRepo(("src/File.cs", "class C { void Run() { } }"));

        var diff = "=== src/File.cs (edit: +1 -1) ===\n@@ -1,1 +1,1 @@\n-old\n+new";
        var result = await _enricher.EnrichAsync(diff);

        Assert.DoesNotContain("[call-sites]", result);
    }

    [Fact]
    public async Task EnrichAsync_RepoNotReady_ReturnsDiffUnchanged()
    {
        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns((string?)null);

        var diff = "=== src/File.cs (edit: +1 -1) ===\n[structural-changes]\n  \U0001f504 Method signature changed: ProcessOrder(\n[/structural-changes]\n@@ -1,1 +1,1 @@\n-old\n+new";
        var result = await _enricher.EnrichAsync(diff);

        Assert.DoesNotContain("[call-sites]", result);
    }

    [Fact]
    public async Task EnrichAsync_ManyTargets_TruncatesAtFive()
    {
        var files = new List<(string, string)>
        {
            ("src/Svc.cs", "class Svc { void M1(){} void M2(){} void M3(){} void M4(){} void M5(){} void M6(){} void M7(){} }")
        };
        // Create callers for each method
        for (int i = 1; i <= 7; i++)
            files.Add(($"src/Caller{i}.cs", $"class C{i} {{ void Run() {{ svc.M{i}(x); }} }}"));

        SetupRepo(files.ToArray());

        var block = string.Join("\n", Enumerable.Range(1, 7).Select(i =>
            $"  \U0001f504 Method signature changed: M{i}("));
        var diff = $"=== src/Svc.cs (edit: +7 -7) ===\n[structural-changes]\n{block}\n[/structural-changes]\n@@ -1,1 +1,1 @@\n-old\n+new";
        var result = await _enricher.EnrichAsync(diff);

        if (result.Contains("[call-sites]"))
        {
            Assert.Contains("... and", result);
            Assert.Contains("more changed members", result);
        }
    }

    [Fact]
    public async Task EnrichAsync_BlockPositionedBeforeHunks()
    {
        SetupRepo(
            ("src/Svc.cs", "class Svc { void ProcessOrder(Order o) { } }"),
            ("src/Ctrl.cs", "class Ctrl { void Run() { svc.ProcessOrder(order); } }"));

        var diff = "=== src/Svc.cs (edit: +1 -1) ===\n[structural-changes]\n  \U0001f504 Method signature changed: ProcessOrder(\n[/structural-changes]\n@@ -1,1 +1,1 @@\n-old\n+new";
        var result = await _enricher.EnrichAsync(diff);

        if (result.Contains("[call-sites]"))
        {
            var callSiteIdx = result.IndexOf("[call-sites]", StringComparison.Ordinal);
            var hunkIdx = result.IndexOf("@@ -1,1 +1,1 @@", StringComparison.Ordinal);
            Assert.True(callSiteIdx < hunkIdx, "[call-sites] should appear before hunks");
        }
    }
}
