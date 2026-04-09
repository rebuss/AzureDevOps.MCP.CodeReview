using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using REBUSS.Pure.Core.Services.CopilotReview;
using REBUSS.Pure.Properties;

namespace REBUSS.Pure.Services.CopilotReview;

/// <summary>
/// Delegates to <see cref="ICopilotClientProvider.TryEnsureStartedAsync"/> and caches
/// the boolean result for the lifetime of the process. Per research.md Decision 6.
/// <para>
/// Phase 2 skeleton — real caching logic arrives in T017 (US1 Phase 3).
/// </para>
/// </summary>
internal sealed class CopilotAvailabilityDetector : ICopilotAvailabilityDetector
{
    private readonly ICopilotClientProvider _clientProvider;
    private readonly IOptions<CopilotReviewOptions> _options;
    private readonly ILogger<CopilotAvailabilityDetector> _logger;

    public CopilotAvailabilityDetector(
        ICopilotClientProvider clientProvider,
        IOptions<CopilotReviewOptions> options,
        ILogger<CopilotAvailabilityDetector> logger)
    {
        _clientProvider = clientProvider;
        _options = options;
        _logger = logger;
    }

    private volatile bool _cached;
    private volatile bool _hasCached;

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_hasCached)
            return _cached;

        // Disabled-by-config short-circuit: do NOT touch the provider (research.md Decision 6).
        if (!_options.Value.Enabled)
        {
            _cached = false;
            _hasCached = true;
            _logger.LogInformation(Resources.LogCopilotNotAvailable, "disabled by configuration");
            return false;
        }

        bool started;
        try
        {
            started = await _clientProvider.TryEnsureStartedAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // cancellation propagates — do not cache
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Copilot client provider threw during availability check");
            started = false;
        }

        _cached = started;
        _hasCached = true;

        if (started)
            _logger.LogInformation(Resources.LogCopilotAvailable);
        else
            _logger.LogInformation(Resources.LogCopilotNotAvailable, "client start failed");

        return started;
    }
}
