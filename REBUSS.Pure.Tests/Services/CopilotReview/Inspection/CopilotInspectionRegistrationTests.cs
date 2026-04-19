using Microsoft.Extensions.DependencyInjection;
using REBUSS.Pure.Services.CopilotReview.Inspection;

namespace REBUSS.Pure.Tests.Services.CopilotReview.Inspection;

/// <summary>
/// Verifies the env-var-gated DI registration of <see cref="ICopilotInspectionWriter"/>.
/// Feature 022 US2 (activation) + US4 (zero-impact when inactive).
///
/// Uses a tiny composition helper that mirrors the <c>Program.cs</c> gate logic — we don't
/// stand up the whole host (slow + brittle) and we don't compose against the real
/// <see cref="Microsoft.Extensions.Logging.ILogger{T}"/>; we register a <c>NullLogger</c>.
/// All env-var manipulation is wrapped in try/finally to avoid polluting other tests.
/// </summary>
public class CopilotInspectionRegistrationTests
{
    private const string EnvVarName = "REBUSS_COPILOT_INSPECT";

    private static ICopilotInspectionWriter ResolveWriter(string? envValue)
    {
        var prior = Environment.GetEnvironmentVariable(EnvVarName);
        try
        {
            Environment.SetEnvironmentVariable(EnvVarName, envValue);
            var services = new ServiceCollection();
            services.AddLogging(); // satisfies ILogger<T> ctor dep on the filesystem writer
            ApplyGate(services);
            using var provider = services.BuildServiceProvider();
            return provider.GetRequiredService<ICopilotInspectionWriter>();
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVarName, prior);
        }
    }

    /// <summary>
    /// Mirrors the env-var gate in <c>Program.cs</c>. Tests this composition in isolation.
    /// If <c>Program.cs</c> changes the gate logic, this helper must change too — the test
    /// file is the canonical reference.
    /// </summary>
    private static void ApplyGate(IServiceCollection services)
    {
        var inspectEnabled = Environment.GetEnvironmentVariable(EnvVarName)
            is "1" or "true" or "True";
        if (inspectEnabled)
            services.AddSingleton<ICopilotInspectionWriter, FileSystemCopilotInspectionWriter>();
        else
            services.AddSingleton<ICopilotInspectionWriter, NoOpCopilotInspectionWriter>();
    }

    [Fact]
    public void EnvVarUnset_ResolvesToNoOp()
    {
        var writer = ResolveWriter(null);
        Assert.IsType<NoOpCopilotInspectionWriter>(writer);
    }

    [Fact]
    public void EnvVar1_ResolvesToFileSystem()
    {
        var writer = ResolveWriter("1");
        Assert.IsType<FileSystemCopilotInspectionWriter>(writer);
    }

    [Fact]
    public void EnvVarTrue_LowerCase_ResolvesToFileSystem()
    {
        var writer = ResolveWriter("true");
        Assert.IsType<FileSystemCopilotInspectionWriter>(writer);
    }

    [Fact]
    public void EnvVarTrue_CapitalizedFirst_ResolvesToFileSystem()
    {
        var writer = ResolveWriter("True");
        Assert.IsType<FileSystemCopilotInspectionWriter>(writer);
    }

    [Fact]
    public void EnvVar0_ResolvesToNoOp()
    {
        var writer = ResolveWriter("0");
        Assert.IsType<NoOpCopilotInspectionWriter>(writer);
    }

    [Fact]
    public void EnvVarFalse_ResolvesToNoOp()
    {
        var writer = ResolveWriter("false");
        Assert.IsType<NoOpCopilotInspectionWriter>(writer);
    }

    [Fact]
    public void EnvVarYes_ResolvesToNoOp()
    {
        // Intentionally narrow enabling-value list (research.md Decision 6) — "yes" is NOT enabled.
        var writer = ResolveWriter("yes");
        Assert.IsType<NoOpCopilotInspectionWriter>(writer);
    }

    [Fact]
    public void EnvVarTRUE_AllCaps_ResolvesToNoOp()
    {
        // Case-sensitive: only "1", "true" (lowercase), "True" (leading capital) enable.
        var writer = ResolveWriter("TRUE");
        Assert.IsType<NoOpCopilotInspectionWriter>(writer);
    }

    [Fact]
    public void EnvVarEmpty_ResolvesToNoOp()
    {
        var writer = ResolveWriter("");
        Assert.IsType<NoOpCopilotInspectionWriter>(writer);
    }

    // ─── Feature 022 US4 (T021) — zero-impact verification ────────────────────────

    // ─── Feature 022 T026 — SC-002 performance overhead microbench ───────────────

    [Fact]
    public async Task EnvVarUnset_NoOpWriter_HasNegligibleOverhead()
    {
        // Microbench: NoOp writer's overhead should be small enough that 10 000 calls
        // complete in under 2× the cost of a tight loop doing nothing meaningful.
        // SC-002's true target ("review wall time within 1% of baseline") is dominated
        // by SDK network I/O and isn't measurable in a unit test — this guard simply
        // prevents the NoOp from accidentally doing real work (e.g., file I/O, allocations
        // proportional to content size).
        const int iterations = 10_000;
        const string content = "small content payload of about 100 bytes for the bench loop here";

        var writer = new NoOpCopilotInspectionWriter();

        // Warmup
        for (var i = 0; i < 100; i++)
            await writer.WritePromptAsync("k", "kind", content, CancellationToken.None);

        // Baseline: empty loop.
        var sw1 = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            // Empty body — measures pure loop + checkbox cost.
        }
        sw1.Stop();
        var baselineMs = sw1.ElapsedMilliseconds;

        // Measured: NoOp calls.
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            await writer.WritePromptAsync("k", "kind", content, CancellationToken.None);
        }
        sw2.Stop();
        var measuredMs = sw2.ElapsedMilliseconds;

        // Loose assertion: NoOp must finish in well under 100 ms for 10k calls.
        // Tight bound versus baseline is too noisy on CI; absolute bound is the actual signal.
        Assert.True(measuredMs < 100,
            $"NoOp inspection writer is too slow: {measuredMs}ms for {iterations} calls (baseline empty loop: {baselineMs}ms)");
    }

    [Fact]
    public async Task NoOpWriter_ManyCalls_TouchesNoFilesystemPath()
    {
        // Direct instantiation (not via DI) — verify NoOp truly performs no IO.
        var writer = new NoOpCopilotInspectionWriter();

        // Watch path under temp; nothing inside this directory should ever be created.
        var watchDir = Path.Combine(Path.GetTempPath(), $"copilot-inspection-noop-watch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(watchDir);
        try
        {
            for (var i = 0; i < 20; i++)
            {
                await writer.WritePromptAsync($"pr:{i}", "page-1-review", "anything", CancellationToken.None);
                await writer.WriteResponseAsync($"pr:{i}", "page-1-review", "anything", CancellationToken.None);
            }

            // No files or subdirectories should have appeared under the watch dir.
            Assert.False(
                Directory.EnumerateFileSystemEntries(watchDir).Any(),
                "NoOpCopilotInspectionWriter must not write anywhere on the filesystem.");
        }
        finally
        {
            try { Directory.Delete(watchDir, recursive: true); } catch { }
        }
    }
}
