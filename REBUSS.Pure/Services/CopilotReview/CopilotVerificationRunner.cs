using GitHub.Copilot.SDK;
using GitHub.Copilot.SDK.Rpc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using REBUSS.Pure.Core.Services.CopilotReview;
using REBUSS.Pure.Properties;

namespace REBUSS.Pure.Services.CopilotReview;

/// <summary>
/// Thin abstraction over the <see cref="CopilotClient"/> operations the
/// verification runner needs. Exists so <see cref="CopilotVerificationRunner"/>
/// can be unit-tested without instantiating the non-mockable real SDK client
/// (which requires a live <c>gh copilot</c> subprocess). Feature 018 T016.
/// </summary>
internal interface ICopilotSdkOps : IAsyncDisposable
{
    /// <summary>
    /// The wrapped real <see cref="CopilotClient"/> — <c>null</c> in test doubles.
    /// Accessed by <c>CopilotClientProvider</c> on the happy path to hand the
    /// live client off to <c>CopilotSessionFactory</c>.
    /// </summary>
    CopilotClient? UnderlyingClient { get; }

    Task StartAsync(CancellationToken ct);
    Task<GetAuthStatusResponse> GetAuthStatusAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> GetModelIdsAsync(CancellationToken ct);

    /// <summary>
    /// Best-effort quota fetch. Returns <c>null</c> when the call fails or the
    /// SDK path is not available. Never throws for SDK errors (but cancellation
    /// still propagates).
    /// </summary>
    Task<AccountGetQuotaResult?> TryGetQuotaAsync(CancellationToken ct);
}

/// <summary>
/// Production <see cref="ICopilotSdkOps"/> wrapping a real
/// <see cref="CopilotClient"/>. Owns the client's lifecycle: constructs it in
/// <see cref="StartAsync"/>, disposes it in <see cref="DisposeAsync"/>.
/// </summary>
internal sealed class CopilotSdkOps : ICopilotSdkOps
{
    private readonly CopilotClientOptions _options;
    private CopilotClient? _client;

    public CopilotSdkOps(CopilotClientOptions options)
    {
        _options = options;
    }

    public CopilotClient? UnderlyingClient => _client;

    public async Task StartAsync(CancellationToken ct)
    {
        _client = new CopilotClient(_options);
        await _client.StartAsync(ct).ConfigureAwait(false);
    }

    public Task<GetAuthStatusResponse> GetAuthStatusAsync(CancellationToken ct) =>
        _client is null
            ? throw new InvalidOperationException("Call StartAsync first.")
            : _client.GetAuthStatusAsync(ct);

    public async Task<IReadOnlyList<string>> GetModelIdsAsync(CancellationToken ct)
    {
        if (_client is null) throw new InvalidOperationException("Call StartAsync first.");
        var models = await _client.ListModelsAsync(ct).ConfigureAwait(false);
        return models.Select(m => m.Id).ToList();
    }

    public async Task<AccountGetQuotaResult?> TryGetQuotaAsync(CancellationToken ct)
    {
        if (_client is null) return null;
        try
        {
            return await _client.Rpc.Account.GetQuotaAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        try { _client?.Dispose(); } catch { /* swallow — best-effort cleanup */ }
        _client = null;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Diagnostic probe surface for callers that only need a <see cref="CopilotVerdict"/>
/// and do NOT want to retain a live Copilot client. Used by
/// <c>CopilotCliSetupStep</c> during <c>rebuss-pure init</c>. Feature 018 T030.
/// </summary>
internal interface ICopilotVerificationProbe
{
    /// <summary>
    /// Runs the same verification as <see cref="CopilotVerificationRunner.BuildAndVerifyAsync"/>
    /// but disposes the underlying client immediately on success, returning only
    /// the verdict. Never throws for strict-mode reasons; cancellation still
    /// propagates.
    /// </summary>
    Task<CopilotVerdict> ProbeAsync(CancellationToken ct = default);
}

/// <summary>
/// Runs the 6-step verification sequence against the Copilot SDK and returns a
/// <see cref="CopilotVerdict"/> plus (on success) the started
/// <see cref="ICopilotSdkOps"/> wrapper so <c>CopilotClientProvider</c> can
/// continue using the live client. Feature 018 T016.
///
/// <para>
/// <b>Sequence</b> (contract C2): token-resolve → build client options →
/// <c>StartAsync</c> → <c>GetAuthStatusAsync</c> → <c>ListModelsAsync</c>
/// intersect against configured model → best-effort <c>GetQuotaAsync</c> →
/// emit either the failure log line (FR-006) or the happy-path info line
/// (FR-013b) → return <c>(ops, verdict)</c>.
/// </para>
///
/// <para>
/// <b>Cancellation (FR-020)</b>: <see cref="OperationCanceledException"/>
/// propagates from any step; the partially-started <see cref="ICopilotSdkOps"/>
/// is disposed before the exception escapes (FR-019); no verdict is cached.
/// </para>
/// </summary>
internal sealed class CopilotVerificationRunner : ICopilotVerificationProbe
{
    private readonly IOptions<CopilotReviewOptions> _options;
    private readonly ICopilotTokenResolver _tokenResolver;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CopilotVerificationRunner> _logger;
    private readonly Func<CopilotClientOptions, ICopilotSdkOps> _opsFactory;

    public CopilotVerificationRunner(
        IOptions<CopilotReviewOptions> options,
        ICopilotTokenResolver tokenResolver,
        ILoggerFactory loggerFactory,
        ILogger<CopilotVerificationRunner> logger,
        Func<CopilotClientOptions, ICopilotSdkOps>? opsFactory = null)
    {
        _options = options;
        _tokenResolver = tokenResolver;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _opsFactory = opsFactory ?? (opts => new CopilotSdkOps(opts));
    }

    /// <summary>
    /// Runs the full verification sequence. On success the caller owns the
    /// returned <see cref="ICopilotSdkOps"/> (including the live
    /// <see cref="CopilotClient"/> inside it) and is responsible for disposal at
    /// shutdown. On failure the ops wrapper is already disposed and the first
    /// element of the tuple is <c>null</c>.
    /// </summary>
    public async Task<(ICopilotSdkOps? Ops, CopilotVerdict Verdict)> BuildAndVerifyAsync(CancellationToken ct)
    {
        var (token, tokenSource) = _tokenResolver.Resolve();
        var configuredModel = _options.Value.Model;

        var clientOptions = new CopilotClientOptions
        {
            Logger = _loggerFactory.CreateLogger("GitHub.Copilot.SDK"),
            GitHubToken = token,
            UseLoggedInUser = token is null,
            AutoStart = true,
        };

        var ops = _opsFactory(clientOptions);

        try
        {
            // Step 3: StartAsync
            try
            {
                await ops.StartAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var verdict = BuildStartFailureVerdict(tokenSource, configuredModel, ex);
                LogFailure(verdict);
                await ops.DisposeAsync().ConfigureAwait(false);
                return (null, verdict);
            }

            // Step 4: GetAuthStatusAsync
            GetAuthStatusResponse authStatus;
            try
            {
                authStatus = await ops.GetAuthStatusAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var verdict = BuildStartFailureVerdict(tokenSource, configuredModel, ex);
                LogFailure(verdict);
                await ops.DisposeAsync().ConfigureAwait(false);
                return (null, verdict);
            }

            if (!authStatus.IsAuthenticated)
            {
                var verdict = BuildNotAuthenticatedVerdict(tokenSource, configuredModel, authStatus);
                LogFailure(verdict);
                await ops.DisposeAsync().ConfigureAwait(false);
                return (null, verdict);
            }

            // Step 5: ListModelsAsync + model entitlement check (FR-002 / FR-004)
            IReadOnlyList<string> modelIds;
            try
            {
                modelIds = await ops.GetModelIdsAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var verdict = BuildStartFailureVerdict(tokenSource, configuredModel, ex);
                LogFailure(verdict);
                await ops.DisposeAsync().ConfigureAwait(false);
                return (null, verdict);
            }

            if (!ModelIsEntitled(modelIds, configuredModel))
            {
                var verdict = BuildModelNotEntitledVerdict(tokenSource, configuredModel, modelIds, authStatus);
                LogFailure(verdict);
                await ops.DisposeAsync().ConfigureAwait(false);
                return (null, verdict);
            }

            // Step 6: best-effort quota check (FR-005) — never gates the verdict
            await CheckQuotaAsync(ops, ct).ConfigureAwait(false);

            // Happy path
            var okVerdict = BuildOkVerdict(tokenSource, configuredModel, modelIds, authStatus);
            LogHappyPath(okVerdict, authStatus);
            return (ops, okVerdict);
        }
        catch (OperationCanceledException)
        {
            // FR-019: dispose the partially-started client before the cancellation escapes.
            // FR-020: caller MUST NOT cache the verdict on cancellation; we never reach a
            // return statement on this path.
            try { await ops.DisposeAsync().ConfigureAwait(false); } catch { /* swallow */ }
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<CopilotVerdict> ProbeAsync(CancellationToken ct = default)
    {
        var (ops, verdict) = await BuildAndVerifyAsync(ct).ConfigureAwait(false);
        if (ops is not null)
        {
            // Diagnostic path: dispose the live client immediately — probes don't need it.
            try { await ops.DisposeAsync().ConfigureAwait(false); } catch { /* swallow */ }
        }
        return verdict;
    }

    private static bool ModelIsEntitled(IReadOnlyList<string> modelIds, string configuredModel) =>
        modelIds.Any(id => string.Equals(id, configuredModel, StringComparison.OrdinalIgnoreCase));

    // --- verdict builders ---

    private static CopilotVerdict BuildStartFailureVerdict(
        CopilotTokenSource source, string configuredModel, Exception ex) =>
        new(
            IsAvailable: false,
            Reason: CopilotAuthReason.StartFailure,
            TokenSource: source,
            ConfiguredModel: configuredModel,
            EntitledModels: Array.Empty<string>(),
            Login: null,
            Host: null,
            Remediation: string.Format(Resources.ErrorCopilotStartFailureRemediation, ex.Message));

    private static CopilotVerdict BuildNotAuthenticatedVerdict(
        CopilotTokenSource source, string configuredModel, GetAuthStatusResponse authStatus)
    {
        // FR-013: when the token came from an override channel we give different
        // remediation text that explicitly warns about classic PATs.
        var remediation = source is CopilotTokenSource.EnvironmentOverride or CopilotTokenSource.ConfigOverride
            ? string.Format(Resources.ErrorCopilotOverrideTokenRejectedRemediation, source)
            : Resources.ErrorCopilotNotAuthenticatedRemediation;
        return new(
            IsAvailable: false,
            Reason: CopilotAuthReason.NotAuthenticated,
            TokenSource: source,
            ConfiguredModel: configuredModel,
            EntitledModels: Array.Empty<string>(),
            Login: authStatus.Login,
            Host: authStatus.Host,
            Remediation: remediation);
    }

    private static CopilotVerdict BuildModelNotEntitledVerdict(
        CopilotTokenSource source, string configuredModel, IReadOnlyList<string> modelIds,
        GetAuthStatusResponse authStatus) =>
        new(
            IsAvailable: false,
            Reason: CopilotAuthReason.ModelNotEntitled,
            TokenSource: source,
            ConfiguredModel: configuredModel,
            EntitledModels: modelIds,
            Login: authStatus.Login,
            Host: authStatus.Host,
            Remediation: string.Format(
                Resources.ErrorCopilotModelNotEntitledRemediation,
                configuredModel, string.Join(", ", modelIds)));

    private static CopilotVerdict BuildOkVerdict(
        CopilotTokenSource source, string configuredModel, IReadOnlyList<string> modelIds,
        GetAuthStatusResponse authStatus) =>
        new(
            IsAvailable: true,
            Reason: CopilotAuthReason.Ok,
            TokenSource: source,
            ConfiguredModel: configuredModel,
            EntitledModels: modelIds,
            Login: authStatus.Login,
            Host: authStatus.Host,
            Remediation: string.Empty);

    // --- quota + logging ---

    private async Task CheckQuotaAsync(ICopilotSdkOps ops, CancellationToken ct)
    {
        AccountGetQuotaResult? quota;
        try
        {
            quota = await ops.TryGetQuotaAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetQuotaAsync failed — continuing without quota check");
            return;
        }

        if (quota is null)
        {
            _logger.LogDebug("GetQuotaAsync returned no snapshot — continuing without quota check");
            return;
        }

        if (LooksLikeFreeTier(quota.QuotaSnapshots))
        {
            var keys = string.Join(",", quota.QuotaSnapshots!.Keys);
            _logger.LogWarning(Resources.LogCopilotFreeTierShapeWarning, keys);
        }
    }

    /// <summary>
    /// Research.md Decision 4: a quota snapshot looks like a free tier when the
    /// <c>premium_interactions</c> key is absent OR its
    /// <c>EntitlementRequests</c> is zero. Heuristic only — never gates the
    /// verdict per FR-005.
    /// </summary>
    internal static bool LooksLikeFreeTier(
        IReadOnlyDictionary<string, AccountGetQuotaResultQuotaSnapshotsValue>? snapshots)
    {
        if (snapshots is null) return false;
        if (!snapshots.TryGetValue("premium_interactions", out var premium))
            return true;
        return premium.EntitlementRequests == 0;
    }

    private void LogFailure(CopilotVerdict verdict)
    {
        // FR-006: single structured remediation log line.
        // FR-013a: no token value in the field set.
        _logger.LogError(
            Resources.LogCopilotReviewUnavailable,
            verdict.Reason,
            verdict.Remediation,
            verdict.TokenSource,
            verdict.ConfiguredModel ?? string.Empty,
            string.Join(", ", verdict.EntitledModels),
            verdict.Login ?? string.Empty,
            verdict.Host ?? string.Empty);
    }

    private void LogHappyPath(CopilotVerdict verdict, GetAuthStatusResponse authStatus)
    {
        // FR-013b: information-level happy-path line with Login/Host + TokenSource + Model.
        // The token value is never a field here.
        _logger.LogInformation(
            Resources.LogCopilotReviewAuthenticatedSuccess,
            verdict.Login ?? string.Empty,
            authStatus.AuthType ?? string.Empty,
            verdict.Host ?? string.Empty,
            verdict.TokenSource,
            verdict.ConfiguredModel ?? string.Empty);
    }
}
