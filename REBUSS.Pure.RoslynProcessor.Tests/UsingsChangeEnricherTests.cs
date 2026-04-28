using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using REBUSS.Pure.Core;

namespace REBUSS.Pure.RoslynProcessor.Tests;

public class UsingsChangeEnricherTests : IDisposable
{
    private readonly IRepositoryDownloadOrchestrator _orchestrator = Substitute.For<IRepositoryDownloadOrchestrator>();
    private readonly DiffSourceResolver _sourceResolver;
    private readonly UsingsChangeEnricher _enricher;
    private readonly string _tempDir;

    public UsingsChangeEnricherTests()
    {
        _sourceResolver = new DiffSourceResolver(
            _orchestrator,
            NullLogger<DiffSourceResolver>.Instance);
        _enricher = new UsingsChangeEnricher(
            _sourceResolver,
            NullLogger<UsingsChangeEnricher>.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), $"usings-test-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void SetupRepo(string fileName, string afterCode)
    {
        var wrapperDir = Path.Combine(_tempDir, "repo");
        var srcDir = Path.Combine(wrapperDir, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, fileName), afterCode);
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
    public async Task EnrichAsync_AddedUsing_InsertsBlock()
    {
        var afterCode = "using System;\nusing System.Text.Json;\nclass C { }";
        SetupRepo("File.cs", afterCode);

        var diff = "=== src/File.cs (edit: +1 -0) ===\n@@ -1,2 +1,3 @@\n using System;\n+using System.Text.Json;\n class C { }";
        var result = await _enricher.EnrichAsync(diff);

        Assert.Contains("[dependency-changes]", result);
        Assert.Contains("System.Text.Json", result);
        Assert.Contains("[/dependency-changes]", result);
    }

    [Fact]
    public async Task EnrichAsync_RemovedUsing_InsertsBlock()
    {
        var afterCode = "using System;\nclass C { }";
        SetupRepo("File.cs", afterCode);

        var diff = "=== src/File.cs (edit: +0 -1) ===\n@@ -1,3 +1,2 @@\n using System;\n-using Newtonsoft.Json;\n class C { }";
        var result = await _enricher.EnrichAsync(diff);

        Assert.Contains("[dependency-changes]", result);
        Assert.Contains("removed Newtonsoft.Json", result);
    }

    [Fact]
    public async Task EnrichAsync_NoChanges_ReturnsDiffUnchanged()
    {
        var afterCode = "using System;\nclass C { void Run() { var x = 1; } }";
        SetupRepo("File.cs", afterCode);

        // Include the using line as context so before-code reconstruction sees it
        var diff = "=== src/File.cs (edit: +1 -1) ===\n@@ -1,2 +1,2 @@\n using System;\n-class C { void Run() { var x = 0; } }\n+class C { void Run() { var x = 1; } }";
        var result = await _enricher.EnrichAsync(diff);

        Assert.DoesNotContain("[dependency-changes]", result);
    }

    [Fact]
    public async Task EnrichAsync_ManyChanges_TruncatesAt10()
    {
        // Create after code with 12 usings
        var usings = string.Join("\n", Enumerable.Range(1, 12).Select(i => $"using Namespace{i};"));
        var afterCode = $"{usings}\nclass C {{ }}";
        SetupRepo("File.cs", afterCode);

        // Diff where all 12 usings are new (before had none)
        var diff = "=== src/File.cs (edit: +12 -0) ===\n@@ -1,1 +1,13 @@\n" +
                   string.Join("\n", Enumerable.Range(1, 12).Select(i => $"+using Namespace{i};")) + "\n class C { }";
        var result = await _enricher.EnrichAsync(diff);

        Assert.Contains("[dependency-changes]", result);
        Assert.Contains("... and 2 more dependency changes", result);
    }

    [Fact]
    public async Task EnrichAsync_StructuralChangesPresent_InsertsBeforeThem()
    {
        var afterCode = "using System;\nusing System.Text.Json;\nclass C { void Run() { } }";
        SetupRepo("File.cs", afterCode);

        var diff = "=== src/File.cs (edit: +2 -1) ===\n[structural-changes]\n  \u2795 New method: Run()\n[/structural-changes]\n@@ -1,2 +1,3 @@\n using System;\n+using System.Text.Json;\n class C { void Run() { } }";
        var result = await _enricher.EnrichAsync(diff);

        var depIdx = result.IndexOf("[dependency-changes]", StringComparison.Ordinal);
        var structIdx = result.IndexOf("[structural-changes]", StringComparison.Ordinal);
        Assert.True(depIdx < structIdx, "[dependency-changes] should appear before [structural-changes]");
    }

    [Fact]
    public async Task EnrichAsync_RepoNotReady_ReturnsDiffUnchanged()
    {
        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns((string?)null);

        var diff = "=== src/File.cs (edit: +1 -1) ===\n@@ -1,1 +1,1 @@\n-old\n+new";
        var result = await _enricher.EnrichAsync(diff);

        Assert.Equal(diff, result);
    }
}
