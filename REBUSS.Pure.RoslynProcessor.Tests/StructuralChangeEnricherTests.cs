using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using REBUSS.Pure.Core;

namespace REBUSS.Pure.RoslynProcessor.Tests;

public class StructuralChangeEnricherTests : IDisposable
{
    private readonly IRepositoryDownloadOrchestrator _orchestrator = Substitute.For<IRepositoryDownloadOrchestrator>();
    private readonly DiffSourceResolver _sourceResolver;
    private readonly StructuralChangeEnricher _enricher;
    private readonly string _tempDir;

    public StructuralChangeEnricherTests()
    {
        _sourceResolver = new DiffSourceResolver(
            _orchestrator,
            NullLogger<DiffSourceResolver>.Instance);
        _enricher = new StructuralChangeEnricher(
            _sourceResolver,
            NullLogger<StructuralChangeEnricher>.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), $"structural-test-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public void CanEnrich_CsFile_ReturnsTrue()
    {
        var diff = "=== src/File.cs (edit: +1 -1) ===\n@@ -1,1 +1,1 @@\n-old\n+new";
        Assert.True(_enricher.CanEnrich(diff));
    }

    [Fact]
    public void CanEnrich_JsonFile_ReturnsFalse()
    {
        var diff = "=== config/settings.json (edit: +1 -1) ===\n@@ -1,1 +1,1 @@\n-old\n+new";
        Assert.False(_enricher.CanEnrich(diff));
    }

    [Fact]
    public void CanEnrich_SkippedFile_ReturnsFalse()
    {
        var diff = "=== src/File.cs (edit: skipped) ===\nReason: full file rewrite";
        Assert.False(_enricher.CanEnrich(diff));
    }

    [Fact]
    public async Task EnrichAsync_MethodSignatureChanged_InsertsAnnotationBlock()
    {
        var wrapperDir = Path.Combine(_tempDir, "repo-abc123");
        var srcDir = Path.Combine(wrapperDir, "src");
        Directory.CreateDirectory(srcDir);

        var afterCode = "class C { void Process(Order o, CancellationToken ct) { } }";
        File.WriteAllText(Path.Combine(srcDir, "Service.cs"), afterCode);

        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns(_tempDir);

        var diff = "=== src/Service.cs (edit: +1 -1) ===\n@@ -1,1 +1,1 @@\n-class C { void Process(Order o) { } }\n+class C { void Process(Order o, CancellationToken ct) { } }";
        var result = await _enricher.EnrichAsync(diff);

        Assert.Contains("[structural-changes]", result);
        Assert.Contains("[/structural-changes]", result);
        Assert.Contains("Process", result);
        // Original diff content preserved
        Assert.Contains("@@ -1,1 +1,1 @@", result);
    }

    [Fact]
    public async Task EnrichAsync_NoStructuralChanges_ReturnsDiffUnchanged()
    {
        var wrapperDir = Path.Combine(_tempDir, "repo-abc123");
        var srcDir = Path.Combine(wrapperDir, "src");
        Directory.CreateDirectory(srcDir);

        // Body-only change — no structural difference
        var afterCode = "class C { void Run() { Console.WriteLine(\"new\"); } }";
        File.WriteAllText(Path.Combine(srcDir, "File.cs"), afterCode);

        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns(_tempDir);

        var diff = "=== src/File.cs (edit: +1 -1) ===\n@@ -1,1 +1,1 @@\n-class C { void Run() { Console.WriteLine(\"old\"); } }\n+class C { void Run() { Console.WriteLine(\"new\"); } }";
        var result = await _enricher.EnrichAsync(diff);

        Assert.DoesNotContain("[structural-changes]", result);
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
    public async Task EnrichAsync_FileNotInRepo_ReturnsDiffUnchanged()
    {
        Directory.CreateDirectory(_tempDir);
        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns(_tempDir);

        var diff = "=== src/Missing.cs (edit: +1 -1) ===\n@@ -1,1 +1,1 @@\n-old\n+new";
        var result = await _enricher.EnrichAsync(diff);

        Assert.Equal(diff, result);
    }

    [Fact]
    public void EnrichAsync_ManyChanges_TruncatesAt15()
    {
        // Test the InsertAnnotationBlock directly with >15 changes
        var changes = Enumerable.Range(1, 20).Select(i => new StructuralChange
        {
            Kind = StructuralChangeKind.MemberAdded,
            Description = $"New method: Method{i}()",
            LineNumber = i
        }).ToList();

        var diff = "=== src/File.cs (edit: +20 -0) ===\n@@ -1,1 +1,20 @@\n+code";
        var result = StructuralChangeEnricher.InsertAnnotationBlock(diff, changes);

        Assert.Contains("[structural-changes]", result);
        Assert.Contains("Method1", result);
        Assert.Contains("Method15", result);
        Assert.DoesNotContain("Method16", result);
        Assert.Contains("... and 5 more structural changes", result);
    }
}
