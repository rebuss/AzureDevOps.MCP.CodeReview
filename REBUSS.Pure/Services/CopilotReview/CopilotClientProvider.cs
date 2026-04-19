using GitHub.Copilot.SDK;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using REBUSS.Pure.Core.Services.CopilotReview;
using REBUSS.Pure.Properties;

namespace REBUSS.Pure.Services.CopilotReview;

/// <summary>
/// Owns the singleton <see cref="CopilotClient"/> for the MCP server process.
/// Lazy-started on first call to <see cref="TryEnsureStartedAsync"/>, gracefully
/// shut down via <see cref="IHostedService.StopAsync"/> + <see cref="IAsyncDisposable"/>.
/// Per research.md Decision 11 and feature 018 T017/T018.
/// <para>
/// On first call, delegates verification to <see cref="CopilotVerificationRunner"/>
/// which runs the 6-step sequence (token-resolve → start → auth-status →
/// model-entitlement → best-effort quota → verdict build + log). The resulting
/// <see cref="CopilotVerdict"/> is cached as <see cref="StartupVerdict"/> and
/// read by <see cref="CopilotAvailabilityDetector"/>.
/// </para>
/// </summary>
internal sealed class CopilotClientProvider : ICopilotClientProvider, IHostedService, IAsyncDisposable
{
    private readonly IOptions<CopilotReviewOptions> _options;
    private readonly CopilotVerificationRunner _runner;
    private readonly ILogger<CopilotClientProvider> _logger;

    private readonly SemaphoreSlim _startGate = new(1, 1);
    private volatile ICopilotSdkOps? _ops;
    private volatile bool _startAttempted;
    private volatile bool _startSucceeded;
    private CopilotVerdict? _startupVerdict;

    public CopilotClientProvider(
        IOptions<CopilotReviewOptions> options,
        CopilotVerificationRunner runner,
        ILogger<CopilotClientProvider> logger)
    {
        _options = options;
        _runner = runner;
        _logger = logger;
    }

    /// <summary>
    /// The verdict produced by the first successful or failed verification run.
    /// <c>null</c> until <see cref="TryEnsureStartedAsync"/> has been called at
    /// least once and completed without cancellation. Read by
    /// <see cref="CopilotAvailabilityDetector"/> via <see cref="ICopilotClientProvider"/>.
    /// Feature 018 T018.
    /// </summary>
    public CopilotVerdict? StartupVerdict => _startupVerdict;

    /// <inheritdoc />
    public async Task<bool> TryEnsureStartedAsync(CancellationToken ct = default)
    {
        // Fast path: start already attempted — return cached outcome without taking the gate.
        if (_startAttempted)
            return _startSucceeded;

        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check after acquiring the gate in case another caller raced ahead.
            if (_startAttempted)
                return _startSucceeded;

            _logger.LogInformation(Resources.LogCopilotClientStarting, _options.Value.Model);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            ICopilotSdkOps? ops;
            CopilotVerdict verdict;
            try
            {
                (ops, verdict) = await _runner.BuildAndVerifyAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // FR-020: do NOT cache the verdict on cancellation. A later non-cancelled
                // caller gets a fresh verification run.
                throw;
            }

            _startupVerdict = verdict;
            _ops = ops;
            _startSucceeded = verdict.IsAvailable && ops is not null;

            sw.Stop();
            if (_startSucceeded)
                _logger.LogInformation(Resources.LogCopilotClientStarted, sw.ElapsedMilliseconds);
            // Failure path already logged by the runner (single FR-006 line).

            _startAttempted = true;
            return _startSucceeded;
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <inheritdoc />
    public object Client
    {
        get
        {
            var ops = _ops;
            if (ops is null || !_startSucceeded)
                throw new InvalidOperationException(
                    "Copilot client not started — call TryEnsureStartedAsync first.");
            var client = ops.UnderlyingClient
                ?? throw new InvalidOperationException(
                    "ICopilotSdkOps has no underlying CopilotClient — test double?");
            return client;
        }
    }

    // IHostedService — lazy start means no work here; shutdown is the interesting hook.
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var ops = _ops;
        if (ops is null) return;
        _logger.LogInformation(Resources.LogCopilotClientStopping);
        try
        {
            // Best-effort dispose through the ops wrapper (which in turn disposes the real
            // CopilotClient). Race against a 5s deadline so shutdown never hangs the host.
            var disposeTask = ops.DisposeAsync().AsTask();
            var completed = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken))
                .ConfigureAwait(false);
            if (completed != disposeTask)
                _logger.LogWarning("Copilot client dispose exceeded 5s during shutdown — abandoned");
            else
                await disposeTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Copilot client stop threw — swallowed during shutdown");
        }
        finally
        {
            _ops = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _startGate.Dispose();
    }
}
