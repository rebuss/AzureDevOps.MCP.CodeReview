using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using REBUSS.Pure.Core;

namespace REBUSS.Pure.RoslynProcessor.Tests;

public class BeforeAfterEnricherTests : IDisposable
{
    private readonly IRepositoryDownloadOrchestrator _orchestrator = Substitute.For<IRepositoryDownloadOrchestrator>();
    private readonly DiffSourceResolver _sourceResolver;
    private readonly BeforeAfterEnricher _enricher;
    private readonly string _tempDir;

    public BeforeAfterEnricherTests()
    {
        _sourceResolver = new DiffSourceResolver(
            _orchestrator,
            NullLogger<DiffSourceResolver>.Instance);
        _enricher = new BeforeAfterEnricher(
            _sourceResolver,
            NullLogger<BeforeAfterEnricher>.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), $"enricher-test-{Guid.NewGuid():N}");
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
    public async Task EnrichAsync_RepoNotAvailable_ReturnsDiffUnchanged()
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
    public async Task EnrichAsync_SemanticChange_AddsContext()
    {
        Directory.CreateDirectory(_tempDir);
        var srcDir = Path.Combine(_tempDir, "src");
        Directory.CreateDirectory(srcDir);

        var sourceCode = string.Join("\n", Enumerable.Range(1, 20).Select(i =>
            i == 10 ? "    void Foo(int x, string y) { }" : $"    // line {i}"))
            .Insert(0, "class C {\n") + "\n}";
        File.WriteAllText(Path.Combine(srcDir, "File.cs"), sourceCode);

        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns(_tempDir);

        var diff = "=== src/File.cs (edit: +1 -1) ===\n@@ -10,1 +10,1 @@\n-    void Foo(int x) { }\n+    void Foo(int x, string y) { }";
        var result = await _enricher.EnrichAsync(diff);

        Assert.Contains("Foo", result);
    }

    [Fact]
    public async Task EnrichAsync_ConcurrentCalls_AllCompleteCorrectly()
    {
        Directory.CreateDirectory(_tempDir);
        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns((string?)null);

        var diffs = Enumerable.Range(1, 10)
            .Select(i => $"=== src/File{i}.cs (edit: +1 -1) ===\n@@ -1,1 +1,1 @@\n-old{i}\n+new{i}")
            .ToArray();

        var tasks = diffs.Select(d => _enricher.EnrichAsync(d)).ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(diffs.Length, results.Length);
        for (int i = 0; i < diffs.Length; i++)
            Assert.Equal(diffs[i], results[i]);
    }
}
