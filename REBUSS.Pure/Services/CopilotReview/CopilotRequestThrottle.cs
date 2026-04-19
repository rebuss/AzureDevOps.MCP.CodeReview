using Microsoft.Extensions.Options;

namespace REBUSS.Pure.Services.CopilotReview;

/// <summary>
/// Ensures that outgoing Copilot SDK requests are spaced at least
/// <see cref="CopilotReviewOptions.MinRequestIntervalSeconds"/> apart, preventing rate-limit
/// violations when multiple page reviews run concurrently. Registered as a singleton; the
/// interval is read from <see cref="IOptions{CopilotReviewOptions}"/> on each call so
/// <c>appsettings.json</c> hot-reload takes effect without restart (Principle V).
/// </summary>
internal sealed class CopilotRequestThrottle
{
    private readonly IOptions<CopilotReviewOptions> _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastRequestTime = DateTimeOffset.MinValue;

    public CopilotRequestThrottle(IOptions<CopilotReviewOptions> options)
    {
        _options = options;
    }

    public async Task WaitAsync(CancellationToken ct)
    {
        var configured = _options.Value.MinRequestIntervalSeconds;
        var minInterval = configured > 0 ? TimeSpan.FromSeconds(configured) : TimeSpan.Zero;
        if (minInterval == TimeSpan.Zero)
            return;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var elapsed = DateTimeOffset.UtcNow - _lastRequestTime;
            if (elapsed < minInterval)
                await Task.Delay(minInterval - elapsed, ct).ConfigureAwait(false);
            _lastRequestTime = DateTimeOffset.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }
}
