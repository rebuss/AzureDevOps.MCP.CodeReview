using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using REBUSS.Pure.Core.Services.CopilotReview;
using REBUSS.Pure.Properties;

namespace REBUSS.Pure.Services.CopilotReview;

/// <summary>
/// Caches the <see cref="CopilotVerdict"/> produced by
/// <see cref="CopilotClientProvider.TryEnsureStartedAsync"/> for the lifetime of
/// the process (FR-007). In graceful mode (<c>CopilotReview:StrictMode = false</c>,
/// the default), <see cref="IsAvailableAsync"/> returns <c>verdict.IsAvailable</c>.
/// In strict mode, a non-available verdict (except <see cref="CopilotAuthReason.DisabledByConfig"/>
/// — see FR-016) translates to a <see cref="CopilotUnavailableException"/> throw.
/// Feature 018 T019 + T027.
/// </summary>
internal sealed class CopilotAvailabilityDetector : ICopilotAvailabilityDetector
{
    private readonly ICopilotClientProvider _clientProvider;
    private readonly IOptions<CopilotReviewOptions> _options;
    private readonly ILogger<CopilotAvailabilityDetector> _logger;

    private readonly object _lock = new();
    private CopilotVerdict? _cachedVerdict;
    private volatile bool _hasCached;

    public CopilotAvailabilityDetector(
        ICopilotClientProvider clientProvider,
        IOptions<CopilotReviewOptions> options,
        ILogger<CopilotAvailabilityDetector> logger)
    {
        _clientProvider = clientProvider;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        var verdict = await GetVerdictAsync(ct).ConfigureAwait(false);

        if (!verdict.IsAvailable
            && verdict.Reason != CopilotAuthReason.DisabledByConfig
            && _options.Value.StrictMode)
        {
            // FR-015 strict mode: throw on real verification failures only.
            // FR-016: disabled-by-config is NOT a verification failure — never throw for it
            // even in strict mode; the handler surfaces its own error for disabled-by-config.
            throw new CopilotUnavailableException(verdict);
        }

        return verdict.IsAvailable;
    }

    /// <inheritdoc />
    public async Task<CopilotVerdict> GetVerdictAsync(CancellationToken ct = default)
    {
        if (_hasCached)
            return _cachedVerdict!;

        // Disabled-by-config short-circuit (FR-016) — do NOT touch the provider.
        if (!_options.Value.Enabled)
        {
            lock (_lock)
            {
                if (!_hasCached)
                {
                    _cachedVerdict = BuildDisabledByConfigVerdict(_options.Value.Model);
                    _hasCached = true;
                    _logger.LogInformation(Resources.LogCopilotNotAvailable, "disabled by configuration");
                }
                return _cachedVerdict!;
            }
        }

        // Delegate to the provider, which in turn delegates to CopilotVerificationRunner.
        // The provider owns the concurrent-first-call race via its own SemaphoreSlim gate;
        // we still protect the verdict write here so the cache is single-assignment.
        try
        {
            await _clientProvider.TryEnsureStartedAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // FR-020: cancellation propagates, cache stays unset for a later retry.
            throw;
        }

        var providerVerdict = _clientProvider.StartupVerdict
            ?? BuildStartFailureFallbackVerdict(_options.Value.Model);

        lock (_lock)
        {
            if (!_hasCached)
            {
                _cachedVerdict = providerVerdict;
                _hasCached = true;
                if (providerVerdict.IsAvailable)
                    _logger.LogInformation(Resources.LogCopilotAvailable);
                // Failure paths already logged by the runner.
            }
            return _cachedVerdict!;
        }
    }

    private static CopilotVerdict BuildDisabledByConfigVerdict(string? configuredModel) =>
        new(
            IsAvailable: false,
            Reason: CopilotAuthReason.DisabledByConfig,
            TokenSource: CopilotTokenSource.None,
            ConfiguredModel: configuredModel,
            EntitledModels: Array.Empty<string>(),
            Login: null,
            Host: null,
            // Deliberately empty — FR-016 behaviour is unchanged from today; no banner, no log.
            Remediation: string.Empty);

    private static CopilotVerdict BuildStartFailureFallbackVerdict(string? configuredModel) =>
        new(
            IsAvailable: false,
            Reason: CopilotAuthReason.StartFailure,
            TokenSource: CopilotTokenSource.LoggedInUser,
            ConfiguredModel: configuredModel,
            EntitledModels: Array.Empty<string>(),
            Login: null,
            Host: null,
            Remediation: "Copilot verification did not produce a verdict (provider returned null).");
}
