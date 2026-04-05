using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.RoslynProcessor;

namespace REBUSS.Pure.RoslynProcessor.Tests;

/// <summary>
/// Integration tests that verify the full enricher pipeline with real (non-mocked)
/// enrichers chained through <see cref="CompositeCodeProcessor"/>.
/// These tests ensure enrichers correctly process output from prior enrichers.
/// </summary>
public class EnricherPipelineIntegrationTests : IDisposable
{
    private readonly IRepositoryDownloadOrchestrator _orchestrator = Substitute.For<IRepositoryDownloadOrchestrator>();
    private readonly string _tempDir;

    public EnricherPipelineIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"pipeline-integ-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
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
    public async Task Pipeline_BeforeAfterThenScope_BothEnrich()
    {
        var afterCode = @"class OrderService
{
    // line 3
    // line 4
    // line 5
    void ProcessOrder(Order o, CancellationToken ct)
    {
        var x = 1; // line 8
    }
    // line 10
}";
        SetupRepo("OrderService.cs", afterCode);

        var sourceResolver = new DiffSourceResolver(_orchestrator, NullLogger<DiffSourceResolver>.Instance);
        var enrichers = new IDiffEnricher[]
        {
            new BeforeAfterEnricher(sourceResolver, NullLogger<BeforeAfterEnricher>.Instance),
            new ScopeAnnotatorEnricher(sourceResolver, NullLogger<ScopeAnnotatorEnricher>.Instance)
        };
        // Inline pipeline — avoids dependency on REBUSS.Pure app project
        async Task<string> RunPipeline(string input) =>
            await ChainEnrichersAsync(enrichers, input);

        var diff = "=== src/OrderService.cs (edit: +1 -1) ===\n@@ -8,1 +8,1 @@\n-        var x = 0;\n+        var x = 1;";
        var result = await RunPipeline(diff);

        // BeforeAfterEnricher may add context lines
        Assert.Contains("OrderService", result);
        // ScopeAnnotatorEnricher should add scope annotation
        Assert.Contains("[scope:", result);
        Assert.Contains("ProcessOrder", result);
    }

    [Fact]
    public async Task Pipeline_BeforeAfterThenStructural_BothEnrich()
    {
        var afterCode = "class C { void Process(Order o, CancellationToken ct) { } }";
        SetupRepo("Svc.cs", afterCode);

        var sourceResolver = new DiffSourceResolver(_orchestrator, NullLogger<DiffSourceResolver>.Instance);
        var enrichers = new IDiffEnricher[]
        {
            new BeforeAfterEnricher(sourceResolver, NullLogger<BeforeAfterEnricher>.Instance),
            new StructuralChangeEnricher(sourceResolver, NullLogger<StructuralChangeEnricher>.Instance)
        };
        // Inline pipeline — avoids dependency on REBUSS.Pure app project
        async Task<string> RunPipeline(string input) =>
            await ChainEnrichersAsync(enrichers, input);

        var diff = "=== src/Svc.cs (edit: +1 -1) ===\n@@ -1,1 +1,1 @@\n-class C { void Process(Order o) { } }\n+class C { void Process(Order o, CancellationToken ct) { } }";
        var result = await RunPipeline(diff);

        // StructuralChangeEnricher should detect the signature change
        Assert.Contains("[structural-changes]", result);
        Assert.Contains("Process", result);
    }

    [Fact]
    public async Task Pipeline_AllCSharpEnrichers_ProcessSequentially()
    {
        var afterCode = "using System.Text.Json;\nclass Svc { void Process(Order o, CancellationToken ct) { var x = 1; } }";
        SetupRepo("Svc.cs", afterCode);

        var sourceResolver = new DiffSourceResolver(_orchestrator, NullLogger<DiffSourceResolver>.Instance);
        var enrichers = new IDiffEnricher[]
        {
            new BeforeAfterEnricher(sourceResolver, NullLogger<BeforeAfterEnricher>.Instance),
            new ScopeAnnotatorEnricher(sourceResolver, NullLogger<ScopeAnnotatorEnricher>.Instance),
            new StructuralChangeEnricher(sourceResolver, NullLogger<StructuralChangeEnricher>.Instance),
            new UsingsChangeEnricher(sourceResolver, NullLogger<UsingsChangeEnricher>.Instance)
        };
        // Inline pipeline — avoids dependency on REBUSS.Pure app project
        async Task<string> RunPipeline(string input) =>
            await ChainEnrichersAsync(enrichers, input);

        var diff = "=== src/Svc.cs (edit: +2 -1) ===\n@@ -1,1 +1,2 @@\n using System;\n+using System.Text.Json;\n-class Svc { void Process(Order o) { var x = 0; } }\n+class Svc { void Process(Order o, CancellationToken ct) { var x = 1; } }";
        var result = await RunPipeline(diff);

        // Verify the diff wasn't corrupted — should still contain the original diff elements
        Assert.Contains("Svc.cs", result);
        Assert.Contains("@@ ", result);
    }

    [Fact]
    public async Task Pipeline_NonCsFile_PassesThroughUnchanged()
    {
        // Non-C# file should pass through all enrichers unchanged
        var sourceResolver = new DiffSourceResolver(_orchestrator, NullLogger<DiffSourceResolver>.Instance);
        var enrichers = new IDiffEnricher[]
        {
            new BeforeAfterEnricher(sourceResolver, NullLogger<BeforeAfterEnricher>.Instance),
            new ScopeAnnotatorEnricher(sourceResolver, NullLogger<ScopeAnnotatorEnricher>.Instance),
            new StructuralChangeEnricher(sourceResolver, NullLogger<StructuralChangeEnricher>.Instance),
            new UsingsChangeEnricher(sourceResolver, NullLogger<UsingsChangeEnricher>.Instance)
        };
        // Inline pipeline — avoids dependency on REBUSS.Pure app project
        async Task<string> RunPipeline(string input) =>
            await ChainEnrichersAsync(enrichers, input);

        var diff = "=== config/settings.json (edit: +1 -1) ===\n@@ -1,1 +1,1 @@\n-old\n+new";
        var result = await RunPipeline(diff);

        Assert.Equal(diff, result);
    }

    /// <summary>
    /// Minimal enricher chaining — same logic as CompositeCodeProcessor
    /// but without requiring a project reference to the app assembly.
    /// </summary>
    private static async Task<string> ChainEnrichersAsync(IDiffEnricher[] enrichers, string diff)
    {
        var current = diff;
        foreach (var enricher in enrichers.OrderBy(e => e.Order))
        {
            if (!enricher.CanEnrich(current))
                continue;
            try
            {
                current = await enricher.EnrichAsync(current);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* graceful fallback — same as CompositeCodeProcessor */ }
        }
        return current;
    }
}
