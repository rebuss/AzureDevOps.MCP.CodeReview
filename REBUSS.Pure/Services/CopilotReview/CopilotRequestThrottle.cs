namespace REBUSS.Pure.Services.CopilotReview;

/// <summary>
/// Ensures that outgoing Copilot SDK requests are spaced at least 3 seconds apart,
/// preventing rate-limit violations when multiple page reviews run concurrently.
/// </summary>
internal static class CopilotRequestThrottle
{
    private static readonly SemaphoreSlim s_gate = new(1, 1);
    private static DateTimeOffset s_lastRequestTime = DateTimeOffset.MinValue;
    private static readonly TimeSpan s_minInterval = TimeSpan.FromSeconds(3);

    public static async Task WaitAsync(CancellationToken ct)
    {
        await s_gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var elapsed = DateTimeOffset.UtcNow - s_lastRequestTime;
            if (elapsed < s_minInterval)
                await Task.Delay(s_minInterval - elapsed, ct).ConfigureAwait(false);
            s_lastRequestTime = DateTimeOffset.UtcNow;
        }
        finally
        {
            s_gate.Release();
        }
    }
}
