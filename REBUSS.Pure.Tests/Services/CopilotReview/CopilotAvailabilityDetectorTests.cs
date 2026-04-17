using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using REBUSS.Pure.Core.Services.CopilotReview;
using REBUSS.Pure.Services.CopilotReview;

namespace REBUSS.Pure.Tests.Services.CopilotReview;

/// <summary>
/// Unit tests for <see cref="CopilotAvailabilityDetector"/> — feature 018 T015 + T025.
/// <para>
/// Covers: disabled-by-config short-circuit (FR-016), verdict caching (FR-007),
/// graceful-mode pass-through, strict-mode throw (FR-015), strict-mode + disabled-by-config
/// NOT throwing (FR-016 vs FR-015 interaction, remediation I1), cancellation not cached
/// (FR-020), and <see cref="ICopilotAvailabilityDetector.GetVerdictAsync"/> never-throws
/// contract.
/// </para>
/// </summary>
public class CopilotAvailabilityDetectorTests
{
    private static CopilotVerdict OkVerdict() => new(
        IsAvailable: true,
        Reason: CopilotAuthReason.Ok,
        TokenSource: CopilotTokenSource.LoggedInUser,
        ConfiguredModel: "claude-sonnet-4.6",
        EntitledModels: new[] { "claude-sonnet-4.6" },
        Login: "octocat",
        Host: "github.com",
        Remediation: string.Empty);

    private static CopilotVerdict NotAuthenticatedVerdict() => new(
        IsAvailable: false,
        Reason: CopilotAuthReason.NotAuthenticated,
        TokenSource: CopilotTokenSource.LoggedInUser,
        ConfiguredModel: "claude-sonnet-4.6",
        EntitledModels: Array.Empty<string>(),
        Login: null,
        Host: null,
        Remediation: "Run 'gh auth login' with Copilot scopes.");

    private static CopilotAvailabilityDetector Create(
        ICopilotClientProvider provider,
        bool enabled = true,
        bool strictMode = false) =>
        new(provider,
            Options.Create(new CopilotReviewOptions { Enabled = enabled, StrictMode = strictMode }),
            NullLogger<CopilotAvailabilityDetector>.Instance);

    private static ICopilotClientProvider StubProvider(CopilotVerdict verdict, bool startResult = true)
    {
        var provider = Substitute.For<ICopilotClientProvider>();
        provider.TryEnsureStartedAsync(Arg.Any<CancellationToken>()).Returns(startResult);
        provider.StartupVerdict.Returns(verdict);
        return provider;
    }

    // ─── Case (a) disabled by config — verdict DisabledByConfig, provider never touched ───

    [Fact]
    public async Task IsAvailableAsync_DisabledByConfig_ReturnsFalseProviderNotCalled()
    {
        var provider = Substitute.For<ICopilotClientProvider>();
        var detector = Create(provider, enabled: false);

        var result = await detector.IsAvailableAsync();

        Assert.False(result);
        await provider.DidNotReceive().TryEnsureStartedAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetVerdictAsync_DisabledByConfig_ReturnsDisabledByConfigVerdict()
    {
        var provider = Substitute.For<ICopilotClientProvider>();
        var detector = Create(provider, enabled: false);

        var verdict = await detector.GetVerdictAsync();

        Assert.False(verdict.IsAvailable);
        Assert.Equal(CopilotAuthReason.DisabledByConfig, verdict.Reason);
        await provider.DidNotReceive().TryEnsureStartedAsync(Arg.Any<CancellationToken>());
    }

    // ─── Case (b) provider returns Ok verdict → IsAvailableAsync = true ───

    [Fact]
    public async Task IsAvailableAsync_ProviderReturnsOkVerdict_ReturnsTrue()
    {
        var provider = StubProvider(OkVerdict(), startResult: true);
        var detector = Create(provider);

        var result = await detector.IsAvailableAsync();
        var verdict = await detector.GetVerdictAsync();

        Assert.True(result);
        // Smoke: enum-to-string coercion produces the canonical name — do NOT use
        // Assert.Same here (string reference equality depends on runtime caching
        // of Enum.ToString()); compare values.
        Assert.Equal("Ok", verdict.Reason.ToString());
        Assert.Equal(CopilotAuthReason.Ok, verdict.Reason);
    }

    // ─── Case (c) unavailable verdict → graceful returns false, no throw ───

    [Fact]
    public async Task IsAvailableAsync_UnavailableVerdict_GracefulModeReturnsFalse()
    {
        var provider = StubProvider(NotAuthenticatedVerdict(), startResult: false);
        var detector = Create(provider, strictMode: false);

        var result = await detector.IsAvailableAsync();

        Assert.False(result);
    }

    // ─── Case (d) verdict cached across calls (FR-007) ───

    [Fact]
    public async Task IsAvailableAsync_VerdictCached_ProviderCalledOnlyOnce()
    {
        var provider = StubProvider(OkVerdict());
        var detector = Create(provider);

        await detector.IsAvailableAsync();
        await detector.IsAvailableAsync();
        await detector.GetVerdictAsync();

        await provider.Received(1).TryEnsureStartedAsync(Arg.Any<CancellationToken>());
    }

    // ─── Case (f) cancellation not cached (FR-020) ───

    [Fact]
    public async Task IsAvailableAsync_CancelledFirstCall_DoesNotCacheAllowsRetry()
    {
        var provider = Substitute.For<ICopilotClientProvider>();
        provider.TryEnsureStartedAsync(Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new OperationCanceledException());
        var detector = Create(provider);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => detector.IsAvailableAsync());

        // Second call — provider no longer throws; verdict should be computed fresh.
        provider.TryEnsureStartedAsync(Arg.Any<CancellationToken>()).Returns(true);
        provider.StartupVerdict.Returns(OkVerdict());

        var result = await detector.IsAvailableAsync();
        Assert.True(result);
        await provider.Received(2).TryEnsureStartedAsync(Arg.Any<CancellationToken>());
    }

    // ─── T025(a) strict mode + available verdict → returns true, no throw ───

    [Fact]
    public async Task IsAvailableAsync_StrictModeVerdictAvailable_ReturnsTrue()
    {
        var provider = StubProvider(OkVerdict());
        var detector = Create(provider, strictMode: true);

        var result = await detector.IsAvailableAsync();

        Assert.True(result);
    }

    // ─── T025(b) strict mode + NotAuthenticated verdict → throws ───

    [Fact]
    public async Task IsAvailableAsync_StrictModeVerdictNotAuthenticated_ThrowsCopilotUnavailable()
    {
        var verdict = NotAuthenticatedVerdict();
        var provider = StubProvider(verdict, startResult: false);
        var detector = Create(provider, strictMode: true);

        var ex = await Assert.ThrowsAsync<CopilotUnavailableException>(
            () => detector.IsAvailableAsync());

        Assert.Equal(verdict, ex.Verdict);
        Assert.Equal(verdict.Remediation, ex.Message);
    }

    [Fact]
    public async Task IsAvailableAsync_StrictModeVerdictModelNotEntitled_ThrowsCopilotUnavailable()
    {
        var verdict = new CopilotVerdict(
            IsAvailable: false,
            Reason: CopilotAuthReason.ModelNotEntitled,
            TokenSource: CopilotTokenSource.LoggedInUser,
            ConfiguredModel: "claude-sonnet-4.6",
            EntitledModels: new[] { "gpt-4o-mini" },
            Login: "octocat",
            Host: "github.com",
            Remediation: "Switch CopilotReview:Model.");
        var provider = StubProvider(verdict, startResult: false);
        var detector = Create(provider, strictMode: true);

        var ex = await Assert.ThrowsAsync<CopilotUnavailableException>(
            () => detector.IsAvailableAsync());
        Assert.Equal(CopilotAuthReason.ModelNotEntitled, ex.Verdict.Reason);
    }

    // ─── T025(c) strict mode cached across throws ───

    [Fact]
    public async Task IsAvailableAsync_StrictModeCachedAcrossThrows_ProviderCalledOnlyOnce()
    {
        var provider = StubProvider(NotAuthenticatedVerdict(), startResult: false);
        var detector = Create(provider, strictMode: true);

        await Assert.ThrowsAsync<CopilotUnavailableException>(() => detector.IsAvailableAsync());
        await Assert.ThrowsAsync<CopilotUnavailableException>(() => detector.IsAvailableAsync());
        await Assert.ThrowsAsync<CopilotUnavailableException>(() => detector.IsAvailableAsync());

        await provider.Received(1).TryEnsureStartedAsync(Arg.Any<CancellationToken>());
    }

    // ─── T025(d) strict mode + Enabled=false → returns FALSE, never throws (FR-016, remediation I1) ───

    [Fact]
    public async Task IsAvailableAsync_StrictModeDisabledByConfig_ReturnsFalseNeverThrows()
    {
        // FR-016: Enabled=false is a short-circuit, not a verification failure.
        // Strict mode MUST NOT escalate an operator-requested disable into a fatal error.
        var provider = Substitute.For<ICopilotClientProvider>();
        var detector = Create(provider, enabled: false, strictMode: true);

        var result = await detector.IsAvailableAsync();

        Assert.False(result);
        await provider.DidNotReceive().TryEnsureStartedAsync(Arg.Any<CancellationToken>());
    }

    // ─── T025(e) GetVerdictAsync never throws for strict-mode reasons ───

    [Fact]
    public async Task GetVerdictAsync_StrictModeUnavailable_DoesNotThrow()
    {
        var provider = StubProvider(NotAuthenticatedVerdict(), startResult: false);
        var detector = Create(provider, strictMode: true);

        // GetVerdictAsync is the diagnostic surface — it MUST NOT translate the verdict
        // into an exception even in strict mode (FR-017 reuse path).
        var verdict = await detector.GetVerdictAsync();

        Assert.False(verdict.IsAvailable);
        Assert.Equal(CopilotAuthReason.NotAuthenticated, verdict.Reason);
    }
}
