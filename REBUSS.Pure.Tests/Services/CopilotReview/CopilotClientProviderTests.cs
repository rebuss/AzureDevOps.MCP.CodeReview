using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using REBUSS.Pure.Services.CopilotReview;

namespace REBUSS.Pure.Tests.Services.CopilotReview;

/// <summary>
/// Unit tests for <see cref="CopilotClientProvider"/> — feature 013 Phase 3 US1 (T026a).
/// <para>
/// <b>Caveat</b>: <c>GitHub.Copilot.SDK.CopilotClient</c> is a concrete non-virtual class
/// that requires a live subprocess to start, so it is not directly mockable. These tests
/// use the <c>clientFactory</c> seam added in T011a to inject either a null factory
/// (never called) or to assert that real-client scenarios would throw cleanly. Deeper
/// coverage of semaphore races / shutdown timeout semantics requires integration testing
/// against a real <c>gh copilot</c> install and is covered by T031 manual smoke instead.
/// </para>
/// <para>
/// The tests here cover what IS unit-testable from the outside: disabled config path,
/// repeated-call caching observable via factory invocation count, and exception propagation
/// from a failing factory.
/// </para>
/// </summary>
public class CopilotClientProviderTests
{
    private static CopilotClientProvider Create(
        Func<CopilotClient>? factory)
    {
        return new CopilotClientProvider(
            Options.Create(new CopilotReviewOptions { Enabled = true, Model = "claude-sonnet-4.6" }),
            NullLoggerFactory.Instance,
            NullLogger<CopilotClientProvider>.Instance,
            factory);
    }

    [Fact]
    public async Task TryEnsureStartedAsync_FactoryThrows_ReturnsFalseNeverThrows()
    {
        var callCount = 0;
        var provider = Create(() =>
        {
            callCount++;
            throw new InvalidOperationException("simulated gh copilot missing");
        });

        var result = await provider.TryEnsureStartedAsync();

        Assert.False(result);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task TryEnsureStartedAsync_FailedStart_CachesFailureForeverAfter()
    {
        var callCount = 0;
        var provider = Create(() =>
        {
            callCount++;
            throw new InvalidOperationException("boom");
        });

        _ = await provider.TryEnsureStartedAsync();
        _ = await provider.TryEnsureStartedAsync();
        _ = await provider.TryEnsureStartedAsync();

        // Factory is invoked exactly once — subsequent calls hit the cached failure fast-path.
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task TryEnsureStartedAsync_ConcurrentCalls_OnlyOneFactoryInvocation()
    {
        var callCount = 0;
        var provider = Create(() =>
        {
            Interlocked.Increment(ref callCount);
            // Simulate a slow failing start so parallel calls pile up at the gate.
            Thread.Sleep(50);
            throw new InvalidOperationException("boom");
        });

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => provider.TryEnsureStartedAsync())
            .ToArray();
        await Task.WhenAll(tasks);

        // Semaphore + _startAttempted flag ensures exactly one factory invocation across 10 racers.
        Assert.Equal(1, callCount);
        Assert.All(tasks, t => Assert.False(t.Result));
    }

    [Fact]
    public void Client_BeforeStart_Throws()
    {
        var provider = Create(() => throw new InvalidOperationException());
        Assert.Throws<InvalidOperationException>(() => _ = provider.Client);
    }

    [Fact]
    public async Task Client_AfterFailedStart_StillThrows()
    {
        var provider = Create(() => throw new InvalidOperationException("boom"));
        _ = await provider.TryEnsureStartedAsync();
        Assert.Throws<InvalidOperationException>(() => _ = provider.Client);
    }

    [Fact]
    public async Task StopAsync_BeforeAnyStart_DoesNotThrow()
    {
        var provider = Create(() => throw new InvalidOperationException());
        // Never called TryEnsureStartedAsync — StopAsync should be a safe no-op.
        await provider.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_AfterFailedStart_DoesNotThrow()
    {
        var provider = Create(() => throw new InvalidOperationException("boom"));
        _ = await provider.TryEnsureStartedAsync();
        // Failed start leaves _client null — StopAsync must handle this gracefully.
        await provider.StopAsync(CancellationToken.None);
    }
}
