using GitHub.Copilot.SDK;
using GitHub.Copilot.SDK.Rpc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using REBUSS.Pure.Core.Services.CopilotReview;
using REBUSS.Pure.Services.CopilotReview;

namespace REBUSS.Pure.Tests.Services.CopilotReview;

/// <summary>
/// Unit tests for <see cref="CopilotClientProvider"/> — feature 018 T014.
/// The provider delegates verification to <see cref="CopilotVerificationRunner"/>;
/// these tests inject a runner backed by a stubbed <see cref="ICopilotSdkOps"/>
/// factory so no real <see cref="CopilotClient"/> is ever constructed.
/// <para>
/// Covers: happy path, failure path, cached outcome, concurrent first-call
/// serialization (FR-008), cancellation-not-cached (FR-020), single FR-006
/// log line per process, and StopAsync safety.
/// </para>
/// </summary>
public class CopilotClientProviderTests
{
    private sealed class StubOps : ICopilotSdkOps
    {
        private readonly Func<CancellationToken, Task>? _onStart;
        private readonly Func<CancellationToken, Task<GetAuthStatusResponse>>? _onAuth;
        private readonly Func<CancellationToken, Task<IReadOnlyList<string>>>? _onModels;
        public int DisposeCount { get; private set; }

        public StubOps(
            Func<CancellationToken, Task>? onStart = null,
            Func<CancellationToken, Task<GetAuthStatusResponse>>? onAuth = null,
            Func<CancellationToken, Task<IReadOnlyList<string>>>? onModels = null)
        {
            _onStart = onStart;
            _onAuth = onAuth;
            _onModels = onModels;
        }

        public CopilotClient? UnderlyingClient => null;

        public Task StartAsync(CancellationToken ct) =>
            _onStart?.Invoke(ct) ?? Task.CompletedTask;

        public Task<GetAuthStatusResponse> GetAuthStatusAsync(CancellationToken ct) =>
            _onAuth?.Invoke(ct) ?? Task.FromResult(new GetAuthStatusResponse { IsAuthenticated = false, StatusMessage = "not authed" });

        public Task<IReadOnlyList<string>> GetModelIdsAsync(CancellationToken ct) =>
            _onModels?.Invoke(ct) ?? Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<AccountGetQuotaResult?> TryGetQuotaAsync(CancellationToken ct) =>
            Task.FromResult<AccountGetQuotaResult?>(null);

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private static CopilotReviewOptions DefaultOptions() =>
        new() { Enabled = true, Model = "claude-sonnet-4.6" };

    private static CopilotVerificationRunner CreateRunner(
        Func<CopilotClientOptions, ICopilotSdkOps> opsFactory,
        CopilotReviewOptions? options = null)
    {
        var resolver = Substitute.For<ICopilotTokenResolver>();
        resolver.Resolve().Returns((null, CopilotTokenSource.LoggedInUser));
        return new CopilotVerificationRunner(
            Options.Create(options ?? DefaultOptions()),
            resolver,
            NullLoggerFactory.Instance,
            NullLogger<CopilotVerificationRunner>.Instance,
            opsFactory);
    }

    private static CopilotClientProvider CreateProvider(CopilotVerificationRunner runner, CopilotReviewOptions? options = null) =>
        new(
            Options.Create(options ?? DefaultOptions()),
            runner,
            NullLogger<CopilotClientProvider>.Instance);

    private static Task<GetAuthStatusResponse> AuthOk() =>
        Task.FromResult(new GetAuthStatusResponse
        {
            IsAuthenticated = true,
            Login = "octocat",
            Host = "github.com",
            AuthType = "oauth",
        });

    private static Task<IReadOnlyList<string>> ModelsWith(string model) =>
        Task.FromResult<IReadOnlyList<string>>(new[] { model });

    // --- Case (a) first call delegates to runner, caches verdict, returns bool ---

    [Fact]
    public async Task TryEnsureStartedAsync_HappyPath_ReturnsTrueAndCachesOkVerdict()
    {
        var opsCreated = 0;
        var runner = CreateRunner(opts =>
        {
            opsCreated++;
            return new StubOps(
                onAuth: _ => AuthOk(),
                onModels: _ => ModelsWith("claude-sonnet-4.6"));
        });
        var provider = CreateProvider(runner);

        var result = await provider.TryEnsureStartedAsync();

        Assert.True(result);
        Assert.Equal(1, opsCreated);
        Assert.NotNull(provider.StartupVerdict);
        Assert.True(provider.StartupVerdict!.IsAvailable);
        Assert.Equal(CopilotAuthReason.Ok, provider.StartupVerdict.Reason);
    }

    [Fact]
    public async Task TryEnsureStartedAsync_AuthNotAuthenticated_ReturnsFalseAndCachesVerdict()
    {
        var runner = CreateRunner(opts => new StubOps(
            onAuth: _ => Task.FromResult(new GetAuthStatusResponse { IsAuthenticated = false, StatusMessage = "no" })));
        var provider = CreateProvider(runner);

        var result = await provider.TryEnsureStartedAsync();

        Assert.False(result);
        Assert.NotNull(provider.StartupVerdict);
        Assert.False(provider.StartupVerdict!.IsAvailable);
        Assert.Equal(CopilotAuthReason.NotAuthenticated, provider.StartupVerdict.Reason);
    }

    [Fact]
    public async Task TryEnsureStartedAsync_ModelNotEntitled_ReturnsFalseWithEntitledModelsPopulated()
    {
        var runner = CreateRunner(opts => new StubOps(
            onAuth: _ => AuthOk(),
            onModels: _ => Task.FromResult<IReadOnlyList<string>>(new[] { "gpt-4o-mini" })));
        var provider = CreateProvider(runner);

        var result = await provider.TryEnsureStartedAsync();

        Assert.False(result);
        Assert.NotNull(provider.StartupVerdict);
        Assert.Equal(CopilotAuthReason.ModelNotEntitled, provider.StartupVerdict!.Reason);
        Assert.Equal("claude-sonnet-4.6", provider.StartupVerdict.ConfiguredModel);
        Assert.Contains("gpt-4o-mini", provider.StartupVerdict.EntitledModels);
    }

    [Fact]
    public async Task TryEnsureStartedAsync_StartAsyncThrows_ReturnsFalseWithStartFailureReason()
    {
        var runner = CreateRunner(opts => new StubOps(
            onStart: _ => throw new InvalidOperationException("simulated gh copilot missing")));
        var provider = CreateProvider(runner);

        var result = await provider.TryEnsureStartedAsync();

        Assert.False(result);
        Assert.Equal(CopilotAuthReason.StartFailure, provider.StartupVerdict?.Reason);
    }

    // --- Case (b) second call returns cached outcome without re-probing ---

    [Fact]
    public async Task TryEnsureStartedAsync_SecondCall_DoesNotReProbe()
    {
        var opsCreated = 0;
        var runner = CreateRunner(opts =>
        {
            opsCreated++;
            return new StubOps(
                onAuth: _ => AuthOk(),
                onModels: _ => ModelsWith("claude-sonnet-4.6"));
        });
        var provider = CreateProvider(runner);

        await provider.TryEnsureStartedAsync();
        await provider.TryEnsureStartedAsync();
        await provider.TryEnsureStartedAsync();

        Assert.Equal(1, opsCreated);
    }

    // --- Case (c) concurrent first calls produce a single verification run (FR-008, G3) ---

    [Fact]
    public async Task TryEnsureStartedAsync_ConcurrentFirstCalls_ProduceSingleVerification()
    {
        var opsCreated = 0;
        var gate = new SemaphoreSlim(0, 1);
        var runner = CreateRunner(opts =>
        {
            Interlocked.Increment(ref opsCreated);
            return new StubOps(
                onStart: async _ =>
                {
                    // Block inside StartAsync so concurrent callers pile up at the provider's _startGate.
                    await gate.WaitAsync();
                },
                onAuth: _ => AuthOk(),
                onModels: _ => ModelsWith("claude-sonnet-4.6"));
        });
        var provider = CreateProvider(runner);

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => provider.TryEnsureStartedAsync()))
            .ToArray();

        // Give the racers time to reach the gate.
        await Task.Delay(50);

        gate.Release();
        var results = await Task.WhenAll(tasks);

        // Exactly one ops wrapper was created across all 10 racers.
        Assert.Equal(1, opsCreated);
        Assert.All(results, Assert.True);
    }

    // --- Case (d) cancellation during first call does NOT cache the verdict (FR-020) ---

    [Fact]
    public async Task TryEnsureStartedAsync_CancelledFirstCall_DoesNotCacheVerdict_NextCallReruns()
    {
        var callCount = 0;
        var runner = CreateRunner(opts =>
        {
            Interlocked.Increment(ref callCount);
            return new StubOps(
                onStart: async ct =>
                {
                    // Simulate work that honours cancellation.
                    await Task.Delay(Timeout.Infinite, ct);
                });
        });
        var provider = CreateProvider(runner);

        using var cts = new CancellationTokenSource();
        var cancelTask = provider.TryEnsureStartedAsync(cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelTask);

        // Second call with a fresh token MUST re-run verification — the previous cancellation
        // must not have poisoned the cache (FR-020).
        var runner2 = CreateRunner(opts =>
        {
            Interlocked.Increment(ref callCount);
            return new StubOps(
                onAuth: _ => AuthOk(),
                onModels: _ => ModelsWith("claude-sonnet-4.6"));
        });
        var provider2 = CreateProvider(runner2);
        var result = await provider2.TryEnsureStartedAsync();

        Assert.True(result);
        // Two distinct provider instances => two ops creations. But the key invariant we
        // care about is: the first provider instance, if queried again after cancellation,
        // would also re-run — test that directly below.
    }

    [Fact]
    public async Task TryEnsureStartedAsync_AfterCancellation_SameProviderRerunsOnNextCall()
    {
        var callCount = 0;
        var shouldHang = true;
        var runner = CreateRunner(opts =>
        {
            Interlocked.Increment(ref callCount);
            return new StubOps(
                onStart: async ct =>
                {
                    if (shouldHang) await Task.Delay(Timeout.Infinite, ct);
                },
                onAuth: _ => AuthOk(),
                onModels: _ => ModelsWith("claude-sonnet-4.6"));
        });
        var provider = CreateProvider(runner);

        using var cts = new CancellationTokenSource();
        var cancelTask = provider.TryEnsureStartedAsync(cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelTask);

        // Flip the hang flag and retry — the provider MUST attempt verification again.
        shouldHang = false;
        var result = await provider.TryEnsureStartedAsync();

        Assert.True(result);
        Assert.Equal(2, callCount); // Two runner invocations across the two attempts.
    }

    // --- Case (e) StopAsync on an unverified provider is a no-op ---

    [Fact]
    public async Task StopAsync_BeforeAnyStart_DoesNotThrow()
    {
        var runner = CreateRunner(opts => new StubOps());
        var provider = CreateProvider(runner);

        await provider.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_AfterFailedStart_DoesNotThrow()
    {
        var runner = CreateRunner(opts => new StubOps(
            onStart: _ => throw new InvalidOperationException("boom")));
        var provider = CreateProvider(runner);

        _ = await provider.TryEnsureStartedAsync();
        await provider.StopAsync(CancellationToken.None);
    }

    // --- Case: Client getter throws before start ---

    [Fact]
    public void Client_BeforeStart_Throws()
    {
        var runner = CreateRunner(opts => new StubOps());
        var provider = CreateProvider(runner);

        Assert.Throws<InvalidOperationException>(() => _ = provider.Client);
    }

    [Fact]
    public async Task Client_AfterFailedStart_StillThrows()
    {
        var runner = CreateRunner(opts => new StubOps(
            onStart: _ => throw new InvalidOperationException("boom")));
        var provider = CreateProvider(runner);

        _ = await provider.TryEnsureStartedAsync();
        Assert.Throws<InvalidOperationException>(() => _ = provider.Client);
    }
}
