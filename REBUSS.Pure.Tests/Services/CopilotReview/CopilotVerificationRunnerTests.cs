using GitHub.Copilot.SDK;
using GitHub.Copilot.SDK.Rpc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using REBUSS.Pure.Core.Services.CopilotReview;
using REBUSS.Pure.Services.CopilotReview;

namespace REBUSS.Pure.Tests.Services.CopilotReview;

/// <summary>
/// Unit tests for <see cref="CopilotVerificationRunner"/> — feature 018 T013.
/// <para>
/// The runner talks to the Copilot SDK through the <see cref="ICopilotSdkOps"/>
/// seam so tests can supply a stub and never touch a real
/// <see cref="CopilotClient"/>. Covers all 6 verification-sequence steps,
/// cancellation, cleanup on failure, call-count budget (SC-004),
/// happy-path log emission (FR-013b), and FR-013a log safety.
/// </para>
/// </summary>
public class CopilotVerificationRunnerTests
{
    private sealed class StubOps : ICopilotSdkOps
    {
        public int StartCalls;
        public int AuthCalls;
        public int ModelCalls;
        public int QuotaCalls;
        public int DisposeCalls;

        public Func<CancellationToken, Task>? OnStart;
        public Func<CancellationToken, Task<GetAuthStatusResponse>>? OnAuth;
        public Func<CancellationToken, Task<IReadOnlyList<string>>>? OnModels;
        public Func<CancellationToken, Task<AccountGetQuotaResult?>>? OnQuota;

        public CopilotClient? UnderlyingClient => null;

        public Task StartAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref StartCalls);
            return OnStart?.Invoke(ct) ?? Task.CompletedTask;
        }
        public Task<GetAuthStatusResponse> GetAuthStatusAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref AuthCalls);
            return OnAuth?.Invoke(ct) ?? Task.FromResult(new GetAuthStatusResponse { IsAuthenticated = false });
        }
        public Task<IReadOnlyList<string>> GetModelIdsAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref ModelCalls);
            return OnModels?.Invoke(ct) ?? Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }
        public Task<AccountGetQuotaResult?> TryGetQuotaAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref QuotaCalls);
            return OnQuota?.Invoke(ct) ?? Task.FromResult<AccountGetQuotaResult?>(null);
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCalls);
            return ValueTask.CompletedTask;
        }
    }

    private static (CopilotVerificationRunner Runner, StubOps Ops) CreateRunner(
        Action<StubOps>? configure = null,
        (string? Token, CopilotTokenSource Source)? tokenOverride = null,
        string configuredModel = "claude-sonnet-4.6",
        ILogger<CopilotVerificationRunner>? logger = null)
    {
        var ops = new StubOps();
        configure?.Invoke(ops);
        var resolver = Substitute.For<ICopilotTokenResolver>();
        resolver.Resolve().Returns(tokenOverride ?? (null, CopilotTokenSource.LoggedInUser));
        var runner = new CopilotVerificationRunner(
            Options.Create(new CopilotReviewOptions { Model = configuredModel }),
            resolver,
            NullLoggerFactory.Instance,
            logger ?? NullLogger<CopilotVerificationRunner>.Instance,
            _ => ops);
        return (runner, ops);
    }

    private static GetAuthStatusResponse Authenticated(string login = "octocat", string host = "github.com") =>
        new()
        {
            IsAuthenticated = true,
            Login = login,
            Host = host,
            AuthType = "oauth",
        };

    // --- Case (a) auth-status IsAuthenticated=false ---

    [Fact]
    public async Task BuildAndVerifyAsync_AuthNotAuthenticated_ReturnsNotAuthenticatedVerdictAndDisposes()
    {
        var (runner, ops) = CreateRunner(o => o.OnAuth = _ =>
            Task.FromResult(new GetAuthStatusResponse { IsAuthenticated = false, StatusMessage = "no session" }));

        var (returnedOps, verdict) = await runner.BuildAndVerifyAsync(CancellationToken.None);

        Assert.Null(returnedOps);
        Assert.False(verdict.IsAvailable);
        Assert.Equal(CopilotAuthReason.NotAuthenticated, verdict.Reason);
        Assert.Equal(1, ops.DisposeCalls);
    }

    // --- Case (b) auth OK, model not entitled ---

    [Fact]
    public async Task BuildAndVerifyAsync_ModelNotEntitled_ReturnsModelNotEntitledVerdictWithEntitledList()
    {
        var (runner, ops) = CreateRunner(o =>
        {
            o.OnAuth = _ => Task.FromResult(Authenticated());
            o.OnModels = _ => Task.FromResult<IReadOnlyList<string>>(new[] { "gpt-4o-mini", "gpt-4o" });
        });

        var (returnedOps, verdict) = await runner.BuildAndVerifyAsync(CancellationToken.None);

        Assert.Null(returnedOps);
        Assert.Equal(CopilotAuthReason.ModelNotEntitled, verdict.Reason);
        Assert.Equal("claude-sonnet-4.6", verdict.ConfiguredModel);
        Assert.Equal(new[] { "gpt-4o-mini", "gpt-4o" }, verdict.EntitledModels);
        Assert.Equal(1, ops.DisposeCalls);
    }

    // --- Case (c) quota throws -> verdict still Ok ---

    [Fact]
    public async Task BuildAndVerifyAsync_QuotaThrows_VerdictIsOkAndNotGated()
    {
        var (runner, ops) = CreateRunner(o =>
        {
            o.OnAuth = _ => Task.FromResult(Authenticated());
            o.OnModels = _ => Task.FromResult<IReadOnlyList<string>>(new[] { "claude-sonnet-4.6" });
            o.OnQuota = _ => throw new InvalidOperationException("quota api not available");
        });

        var (returnedOps, verdict) = await runner.BuildAndVerifyAsync(CancellationToken.None);

        Assert.NotNull(returnedOps);
        Assert.True(verdict.IsAvailable);
        Assert.Equal(CopilotAuthReason.Ok, verdict.Reason);
    }

    // --- Case (d) quota looks like free tier -> verdict Ok + warning ---

    [Fact]
    public async Task BuildAndVerifyAsync_QuotaFreeTierShape_VerdictIsOkWarningLogged()
    {
        var warnCount = 0;
        var logger = Substitute.For<ILogger<CopilotVerificationRunner>>();
        logger.When(l => l.Log(
                LogLevel.Warning, Arg.Any<EventId>(), Arg.Any<object>(),
                Arg.Any<Exception?>(), Arg.Any<Func<object, Exception?, string>>()))
            .Do(_ => Interlocked.Increment(ref warnCount));

        var (runner, _) = CreateRunner(o =>
        {
            o.OnAuth = _ => Task.FromResult(Authenticated());
            o.OnModels = _ => Task.FromResult<IReadOnlyList<string>>(new[] { "claude-sonnet-4.6" });
            // Snapshot without premium_interactions key => free tier shape per Decision 4.
            o.OnQuota = _ => Task.FromResult<AccountGetQuotaResult?>(new AccountGetQuotaResult
            {
                QuotaSnapshots = new Dictionary<string, AccountGetQuotaResultQuotaSnapshotsValue>
                {
                    ["chat"] = new() { EntitlementRequests = 50, UsedRequests = 0 }
                }
            });
        }, logger: logger);

        var (returnedOps, verdict) = await runner.BuildAndVerifyAsync(CancellationToken.None);

        Assert.True(verdict.IsAvailable);
        Assert.True(warnCount >= 1, "Expected at least one Warning log for free-tier quota shape.");
    }

    // --- Case (e) StartAsync throws -> StartFailure + cleanup ---

    [Fact]
    public async Task BuildAndVerifyAsync_StartAsyncThrows_ReturnsStartFailureVerdictAndDisposes()
    {
        var (runner, ops) = CreateRunner(o =>
            o.OnStart = _ => throw new InvalidOperationException("gh copilot missing"));

        var (returnedOps, verdict) = await runner.BuildAndVerifyAsync(CancellationToken.None);

        Assert.Null(returnedOps);
        Assert.Equal(CopilotAuthReason.StartFailure, verdict.Reason);
        Assert.Equal(1, ops.DisposeCalls);
    }

    // --- Case (f) cancellation during GetAuthStatusAsync -> propagates, client disposed, no verdict ---

    [Fact]
    public async Task BuildAndVerifyAsync_CancelledDuringAuthStatus_PropagatesAndDisposes()
    {
        var (runner, ops) = CreateRunner(o => o.OnAuth = async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Authenticated();
        });
        using var cts = new CancellationTokenSource();

        var task = runner.BuildAndVerifyAsync(cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        Assert.Equal(1, ops.DisposeCalls);
    }

    // --- Case (g) happy path: Login/Host populated + exactly one Info log line (FR-013b, G1) ---

    [Fact]
    public async Task BuildAndVerifyAsync_HappyPath_ReturnsOkVerdictWithLoginAndHostAndEmitsInfoLog()
    {
        var infoCount = 0;
        var errorCount = 0;
        var logger = Substitute.For<ILogger<CopilotVerificationRunner>>();
        logger.When(l => l.Log(
                LogLevel.Information, Arg.Any<EventId>(), Arg.Any<object>(),
                Arg.Any<Exception?>(), Arg.Any<Func<object, Exception?, string>>()))
            .Do(_ => Interlocked.Increment(ref infoCount));
        logger.When(l => l.Log(
                LogLevel.Error, Arg.Any<EventId>(), Arg.Any<object>(),
                Arg.Any<Exception?>(), Arg.Any<Func<object, Exception?, string>>()))
            .Do(_ => Interlocked.Increment(ref errorCount));

        var (runner, _) = CreateRunner(o =>
        {
            o.OnAuth = _ => Task.FromResult(Authenticated(login: "testuser", host: "github.com"));
            o.OnModels = _ => Task.FromResult<IReadOnlyList<string>>(new[] { "claude-sonnet-4.6" });
        }, logger: logger);

        var (returnedOps, verdict) = await runner.BuildAndVerifyAsync(CancellationToken.None);

        Assert.NotNull(returnedOps);
        Assert.True(verdict.IsAvailable);
        Assert.Equal(CopilotAuthReason.Ok, verdict.Reason);
        Assert.Equal("testuser", verdict.Login);
        Assert.Equal("github.com", verdict.Host);
        Assert.Equal(1, infoCount);
        Assert.Equal(0, errorCount);
    }

    // --- Case (h) SDK call-count budget (SC-004 / G2) ---

    [Fact]
    public async Task BuildAndVerifyAsync_HappyPath_CallsSdkExactlyOnce()
    {
        var (runner, ops) = CreateRunner(o =>
        {
            o.OnAuth = _ => Task.FromResult(Authenticated());
            o.OnModels = _ => Task.FromResult<IReadOnlyList<string>>(new[] { "claude-sonnet-4.6" });
        });

        await runner.BuildAndVerifyAsync(CancellationToken.None);

        Assert.Equal(1, ops.StartCalls);
        Assert.Equal(1, ops.AuthCalls);
        Assert.Equal(1, ops.ModelCalls);
        Assert.True(ops.QuotaCalls <= 1, $"Expected at most one quota call, got {ops.QuotaCalls}");
    }

    // --- Override path selects correct remediation (US2 precursor, G5) ---

    [Fact]
    public async Task BuildAndVerifyAsync_OverrideTokenRejected_RemediationNamesClassicPatWarning()
    {
        var (runner, _) = CreateRunner(
            o => o.OnAuth = _ => Task.FromResult(new GetAuthStatusResponse { IsAuthenticated = false }),
            tokenOverride: ("bad-token", CopilotTokenSource.EnvironmentOverride));

        var (_, verdict) = await runner.BuildAndVerifyAsync(CancellationToken.None);

        Assert.Equal(CopilotTokenSource.EnvironmentOverride, verdict.TokenSource);
        Assert.Contains("Classic GitHub Personal Access Tokens", verdict.Remediation);
    }

    [Fact]
    public async Task BuildAndVerifyAsync_LoggedInUserAuthFails_RemediationPointsToGhAuthLogin()
    {
        var (runner, _) = CreateRunner(
            o => o.OnAuth = _ => Task.FromResult(new GetAuthStatusResponse { IsAuthenticated = false }));

        var (_, verdict) = await runner.BuildAndVerifyAsync(CancellationToken.None);

        Assert.Equal(CopilotTokenSource.LoggedInUser, verdict.TokenSource);
        Assert.Contains("gh auth login", verdict.Remediation);
    }

    // --- FR-013a: token value never in verdict/remediation/ToString ---

    [Fact]
    public async Task BuildAndVerifyAsync_TokenValueNeverAppearsInVerdictOrLogFields()
    {
        const string sentinel = "SENTINEL_TOKEN_VALUE_MUST_NOT_LEAK";
        var (runner, _) = CreateRunner(
            o => o.OnAuth = _ => Task.FromResult(new GetAuthStatusResponse { IsAuthenticated = false }),
            tokenOverride: (sentinel, CopilotTokenSource.EnvironmentOverride));

        var (_, verdict) = await runner.BuildAndVerifyAsync(CancellationToken.None);

        Assert.DoesNotContain(sentinel, verdict.Remediation);
        Assert.DoesNotContain(sentinel, verdict.ToString());
    }

    // --- LooksLikeFreeTier pure heuristic ---

    [Fact]
    public void LooksLikeFreeTier_NullSnapshots_ReturnsFalse()
    {
        Assert.False(CopilotVerificationRunner.LooksLikeFreeTier(null));
    }

    [Fact]
    public void LooksLikeFreeTier_NoPremiumKey_ReturnsTrue()
    {
        var snapshots = new Dictionary<string, AccountGetQuotaResultQuotaSnapshotsValue>
        {
            ["chat"] = new() { EntitlementRequests = 50 }
        };
        Assert.True(CopilotVerificationRunner.LooksLikeFreeTier(snapshots));
    }

    [Fact]
    public void LooksLikeFreeTier_PremiumKeyZeroEntitlement_ReturnsTrue()
    {
        var snapshots = new Dictionary<string, AccountGetQuotaResultQuotaSnapshotsValue>
        {
            ["premium_interactions"] = new() { EntitlementRequests = 0 }
        };
        Assert.True(CopilotVerificationRunner.LooksLikeFreeTier(snapshots));
    }

    [Fact]
    public void LooksLikeFreeTier_PremiumKeyPositiveEntitlement_ReturnsFalse()
    {
        var snapshots = new Dictionary<string, AccountGetQuotaResultQuotaSnapshotsValue>
        {
            ["premium_interactions"] = new() { EntitlementRequests = 300 }
        };
        Assert.False(CopilotVerificationRunner.LooksLikeFreeTier(snapshots));
    }

    // --- CliPath override (bundled-CLI fallback for missing/broken SDK package) ---

    private static (CopilotVerificationRunner Runner, StubOps Ops, List<CopilotClientOptions> Captured)
        CreateRunnerCapturingOptions(
            CopilotReviewOptions options,
            Func<string, string?>? envReader = null,
            Action<StubOps>? configure = null)
    {
        var ops = new StubOps
        {
            OnAuth = _ => Task.FromResult(Authenticated()),
            OnModels = _ => Task.FromResult<IReadOnlyList<string>>(new[] { options.Model }),
        };
        configure?.Invoke(ops);
        var captured = new List<CopilotClientOptions>();
        var resolver = Substitute.For<ICopilotTokenResolver>();
        resolver.Resolve().Returns((null, CopilotTokenSource.LoggedInUser));
        var runner = new CopilotVerificationRunner(
            Options.Create(options),
            resolver,
            NullLoggerFactory.Instance,
            NullLogger<CopilotVerificationRunner>.Instance,
            opts => { captured.Add(opts); return ops; },
            envReader ?? (_ => null));
        return (runner, ops, captured);
    }

    [Fact]
    public async Task BuildAndVerifyAsync_NoCliPathOverride_LeavesClientOptionsCliPathNull()
    {
        var (runner, _, captured) = CreateRunnerCapturingOptions(
            new CopilotReviewOptions { Model = "claude-sonnet-4.6" });

        await runner.BuildAndVerifyAsync(CancellationToken.None);

        Assert.Single(captured);
        Assert.Null(captured[0].CliPath);
    }

    [Fact]
    public async Task BuildAndVerifyAsync_ConfigCliPath_PropagatedToClientOptions()
    {
        var (runner, _, captured) = CreateRunnerCapturingOptions(
            new CopilotReviewOptions
            {
                Model = "claude-sonnet-4.6",
                CopilotCliPath = @"C:\tools\copilot.exe",
            });

        await runner.BuildAndVerifyAsync(CancellationToken.None);

        Assert.Equal(@"C:\tools\copilot.exe", captured[0].CliPath);
    }

    [Fact]
    public async Task BuildAndVerifyAsync_EnvCliPath_OverridesConfigCliPath()
    {
        var (runner, _, captured) = CreateRunnerCapturingOptions(
            new CopilotReviewOptions
            {
                Model = "claude-sonnet-4.6",
                CopilotCliPath = @"C:\from-config\copilot.exe",
            },
            envReader: name => name == CopilotReviewOptions.CopilotCliPathEnvironmentVariable
                ? @"C:\from-env\copilot.exe"
                : null);

        await runner.BuildAndVerifyAsync(CancellationToken.None);

        Assert.Equal(@"C:\from-env\copilot.exe", captured[0].CliPath);
    }

    [Fact]
    public async Task BuildAndVerifyAsync_BlankEnvCliPath_FallsBackToConfigValue()
    {
        var (runner, _, captured) = CreateRunnerCapturingOptions(
            new CopilotReviewOptions
            {
                Model = "claude-sonnet-4.6",
                CopilotCliPath = @"C:\from-config\copilot.exe",
            },
            envReader: name => name == CopilotReviewOptions.CopilotCliPathEnvironmentVariable
                ? "   "
                : null);

        await runner.BuildAndVerifyAsync(CancellationToken.None);

        Assert.Equal(@"C:\from-config\copilot.exe", captured[0].CliPath);
    }

    [Fact]
    public async Task BuildAndVerifyAsync_BlankConfigAndEnv_LeavesClientOptionsCliPathNull()
    {
        var (runner, _, captured) = CreateRunnerCapturingOptions(
            new CopilotReviewOptions { Model = "claude-sonnet-4.6", CopilotCliPath = "  " },
            envReader: _ => "");

        await runner.BuildAndVerifyAsync(CancellationToken.None);

        Assert.Null(captured[0].CliPath);
    }
}
