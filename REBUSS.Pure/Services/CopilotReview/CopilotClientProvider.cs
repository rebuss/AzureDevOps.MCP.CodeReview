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
/// Per research.md Decision 11.
/// <para>
/// This is the Phase 2 skeleton — the real body of <see cref="TryEnsureStartedAsync"/>
/// lives in T017a (US1 Phase 3). Stub currently returns <c>false</c> unconditionally.
/// </para>
/// </summary>
internal sealed class CopilotClientProvider : ICopilotClientProvider, IHostedService, IAsyncDisposable
{
    private readonly IOptions<CopilotReviewOptions> _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CopilotClientProvider> _logger;
    private readonly Func<CopilotClient> _clientFactory;

    private readonly SemaphoreSlim _startGate = new(1, 1);
    private volatile CopilotClient? _client;
    private volatile bool _startAttempted;
    private volatile bool _startSucceeded;
    private Exception? _startException;

    public CopilotClientProvider(
        IOptions<CopilotReviewOptions> options,
        ILoggerFactory loggerFactory,
        ILogger<CopilotClientProvider> logger,
        Func<CopilotClient>? clientFactory = null)
    {
        _options = options;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _clientFactory = clientFactory ?? (() => new CopilotClient(new CopilotClientOptions
        {
            Logger = loggerFactory.CreateLogger("GitHub.Copilot.SDK"),
            UseLoggedInUser = true,
            AutoStart = true,
        }));
    }

    /// <inheritdoc />
    public async Task<bool> TryEnsureStartedAsync(CancellationToken ct = default)
    {
        // Fast path: start already attempted — return cached outcome without taking the gate.
        if (_startAttempted)
            return _startSucceeded;

        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check after acquiring the gate in case another caller started in parallel.
            if (_startAttempted)
                return _startSucceeded;

            _logger.LogInformation(Resources.LogCopilotClientStarting, _options.Value.Model);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            CopilotClient? client = null;
            try
            {
                client = _clientFactory();
                await client.StartAsync(ct).ConfigureAwait(false);
                _client = client;
                _startSucceeded = true;
                sw.Stop();
                _logger.LogInformation(Resources.LogCopilotClientStarted, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _startException = ex;
                _startSucceeded = false;
                _logger.LogWarning(Resources.LogCopilotClientStartFailed, ex.Message);
                // Best-effort disposal of the partially-started client.
                if (client is not null)
                {
                    try { client.Dispose(); } catch { /* swallow */ }
                }
            }
            finally
            {
                _startAttempted = true;
            }

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
            var client = _client;
            if (client is null || !_startSucceeded)
                throw new InvalidOperationException(
                    "Copilot client not started — call TryEnsureStartedAsync first.");
            return client;
        }
    }

    // IHostedService — lazy start means no work here; shutdown is the interesting hook.
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var client = _client;
        if (client is null) return;
        _logger.LogInformation(Resources.LogCopilotClientStopping);
        try
        {
            // CopilotClient.StopAsync is parameterless — race it against a 5s timeout so shutdown
            // never hangs the MCP server process. On timeout, fall back to ForceStopAsync.
            var stopTask = client.StopAsync();
            var completed = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken))
                .ConfigureAwait(false);
            if (completed != stopTask)
            {
                _logger.LogWarning("Copilot client StopAsync exceeded 5s — falling back to ForceStopAsync");
                try { await client.ForceStopAsync().ConfigureAwait(false); } catch { /* swallow */ }
            }
            else
            {
                // Observe any exception from the completed stop task.
                await stopTask.ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Copilot client stop threw — swallowed during shutdown");
        }
        finally
        {
            _client = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _startGate.Dispose();
        _client?.Dispose();
    }
}
