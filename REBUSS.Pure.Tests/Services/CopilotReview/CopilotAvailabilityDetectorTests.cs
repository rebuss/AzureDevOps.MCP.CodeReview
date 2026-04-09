using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using REBUSS.Pure.Core.Services.CopilotReview;
using REBUSS.Pure.Services.CopilotReview;

namespace REBUSS.Pure.Tests.Services.CopilotReview;

/// <summary>
/// Unit tests for <see cref="CopilotAvailabilityDetector"/> — feature 013 Phase 3 US1 (T026).
/// Mocks <see cref="ICopilotClientProvider"/> at the interface boundary.
/// </summary>
public class CopilotAvailabilityDetectorTests
{
    private static CopilotAvailabilityDetector Create(
        ICopilotClientProvider provider,
        bool enabled = true) =>
        new(provider,
            Options.Create(new CopilotReviewOptions { Enabled = enabled }),
            NullLogger<CopilotAvailabilityDetector>.Instance);

    [Fact]
    public async Task IsAvailable_ClientProviderStarts_ReturnsTrue()
    {
        var provider = Substitute.For<ICopilotClientProvider>();
        provider.TryEnsureStartedAsync(Arg.Any<CancellationToken>()).Returns(true);

        var detector = Create(provider);
        var result = await detector.IsAvailableAsync();

        Assert.True(result);
    }

    [Fact]
    public async Task IsAvailable_ClientProviderFailsToStart_ReturnsFalse()
    {
        var provider = Substitute.For<ICopilotClientProvider>();
        provider.TryEnsureStartedAsync(Arg.Any<CancellationToken>()).Returns(false);

        var detector = Create(provider);
        var result = await detector.IsAvailableAsync();

        Assert.False(result);
    }

    [Fact]
    public async Task IsAvailable_ClientProviderThrows_ReturnsFalseNeverThrows()
    {
        var provider = Substitute.For<ICopilotClientProvider>();
        provider.TryEnsureStartedAsync(Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("boom"));

        var detector = Create(provider);
        var result = await detector.IsAvailableAsync();

        Assert.False(result);
    }

    [Fact]
    public async Task IsAvailable_ResultCached_SecondCallDoesNotInvokeProviderAgain()
    {
        var provider = Substitute.For<ICopilotClientProvider>();
        provider.TryEnsureStartedAsync(Arg.Any<CancellationToken>()).Returns(true);

        var detector = Create(provider);
        _ = await detector.IsAvailableAsync();
        _ = await detector.IsAvailableAsync();
        _ = await detector.IsAvailableAsync();

        await provider.Received(1).TryEnsureStartedAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IsAvailable_DisabledByConfig_ReturnsFalseProviderNotCalled()
    {
        var provider = Substitute.For<ICopilotClientProvider>();
        var detector = Create(provider, enabled: false);

        var result = await detector.IsAvailableAsync();

        Assert.False(result);
        await provider.DidNotReceive().TryEnsureStartedAsync(Arg.Any<CancellationToken>());
    }

    // ─── Feature 013 Phase 4 US2 (T032) — cache SLA guard ────────────────────────

    [Fact]
    public async Task IsAvailable_FirstCallTakesLatency_SecondCallIsCacheHit()
    {
        // SC-003: exactly one probe per process; subsequent calls should be effectively free.
        var provider = Substitute.For<ICopilotClientProvider>();
        provider.TryEnsureStartedAsync(Arg.Any<CancellationToken>())
            .Returns(async _ => { await Task.Delay(30); return true; });

        var detector = Create(provider);

        var sw1 = System.Diagnostics.Stopwatch.StartNew();
        _ = await detector.IsAvailableAsync();
        sw1.Stop();

        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        _ = await detector.IsAvailableAsync();
        sw2.Stop();

        // Second call returns from the cached bool — no provider re-invocation, negligible time.
        await provider.Received(1).TryEnsureStartedAsync(Arg.Any<CancellationToken>());
        Assert.True(sw2.ElapsedMilliseconds < sw1.ElapsedMilliseconds,
            $"Cache hit took {sw2.ElapsedMilliseconds}ms vs first call {sw1.ElapsedMilliseconds}ms");
    }
}
