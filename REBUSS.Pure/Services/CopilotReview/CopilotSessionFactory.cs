using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using REBUSS.Pure.Core.Services.CopilotReview;

namespace REBUSS.Pure.Services.CopilotReview;

/// <summary>
/// Production implementation of <see cref="ICopilotSessionFactory"/>. Wraps
/// <see cref="CopilotClient.CreateSessionAsync(SessionConfig, CancellationToken)"/>
/// with a <see cref="CopilotSessionHandle"/> adapter so <c>REBUSS.Pure.Core</c>
/// never depends on the concrete <see cref="CopilotSession"/> type. Per research.md Decision 8.
/// <para>
/// <b>Thread-safety:</b> <see cref="CreateSessionAsync"/> is safe to call concurrently
/// and is in fact called that way by <c>CopilotReviewOrchestrator</c> (parallel page
/// batches). Reasoning, in order of the call:
/// <list type="bullet">
///   <item><see cref="ICopilotClientProvider.TryEnsureStartedAsync"/> is already
///   internally serialized by a <c>SemaphoreSlim</c> gate (see
///   <c>CopilotClientProvider._startGate</c> and its double-check pattern).</item>
///   <item><see cref="ICopilotClientProvider.Client"/> is a <c>volatile</c> field read,
///   published once by the gate-holder — concurrent getters observe a stable reference.</item>
///   <item>This class carries no mutable instance state — the method body builds a fresh
///   <see cref="SessionConfig"/> on the stack; no shared fields are mutated.</item>
///   <item><see cref="CopilotRequestThrottle.WaitAsync"/> serializes the outbound spacing
///   (configurable via <see cref="CopilotReviewOptions.MinRequestIntervalSeconds"/>)
///   but does not itself require sequential callers.</item>
///   <item>The SDK call <see cref="CopilotClient.CreateSessionAsync(SessionConfig,CancellationToken)"/>
///   is the only remaining boundary; we rely on it being concurrency-safe (which matches
///   observed production behaviour under parallel page dispatch).</item>
/// </list>
/// </para>
/// </summary>
internal sealed class CopilotSessionFactory : ICopilotSessionFactory
{
    private readonly ICopilotClientProvider _provider;
    private readonly IOptions<CopilotReviewOptions> _options;
    private readonly CopilotRequestThrottle _throttle;
    private readonly ILogger<CopilotSessionFactory> _logger;

    public CopilotSessionFactory(
        ICopilotClientProvider provider,
        IOptions<CopilotReviewOptions> options,
        CopilotRequestThrottle throttle,
        ILogger<CopilotSessionFactory> logger)
    {
        _provider = provider;
        _options = options;
        _throttle = throttle;
        _logger = logger;
    }

    public async Task<ICopilotSessionHandle> CreateSessionAsync(string model, CancellationToken ct)
    {
        var started = await _provider.TryEnsureStartedAsync(ct).ConfigureAwait(false);
        if (!started)
            throw new InvalidOperationException(
                "Copilot client is not started — CopilotAvailabilityDetector.IsAvailableAsync should gate this call.");

        var client = (CopilotClient)_provider.Client;

        // Diagnostic: log available models at Debug level.
        // Upstream CopilotVerificationRunner already verified model entitlement per
        // research.md Decision 6 (feature 018 T020); a warning here would be redundant.
        try
        {
            var models = await client.ListModelsAsync(ct).ConfigureAwait(false);
            var modelList = string.Join(", ", models.Select(m => m.Id));
            _logger.LogDebug("Copilot SDK available models: [{Models}]", modelList);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ListModelsAsync failed — model availability unknown");
        }

        var config = new SessionConfig
        {
            Model = model,
            OnPermissionRequest = PermissionHandler.ApproveAll,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
            Streaming = false,
        };

        await _throttle.WaitAsync(ct).ConfigureAwait(false);
        var session = await client.CreateSessionAsync(config, ct).ConfigureAwait(false);
        return new CopilotSessionHandle(session, client, _throttle, _logger);
    }
}

/// <summary>
/// Production adapter wrapping a concrete <see cref="CopilotSession"/> behind the
/// <see cref="ICopilotSessionHandle"/> abstraction. Forwards <see cref="SendAsync"/>
/// to <see cref="CopilotSession.SendAsync(MessageOptions, CancellationToken)"/>, bridges
/// the strongly-typed <see cref="SessionEventHandler"/> delegate to our
/// <c>Action&lt;object&gt;</c>-based subscriber contract, and propagates
/// <see cref="IAsyncDisposable"/> to the session.
/// </summary>
internal sealed class CopilotSessionHandle : ICopilotSessionHandle
{
    private readonly CopilotSession _session;
    private readonly CopilotClient _client;
    private readonly CopilotRequestThrottle _throttle;
    private readonly ILogger _logger;

    public CopilotSessionHandle(CopilotSession session, CopilotClient client, CopilotRequestThrottle throttle, ILogger logger)
    {
        _session = session;
        _client = client;
        _throttle = throttle;
        _logger = logger;
    }

    public async Task<string> SendAsync(string prompt, CancellationToken ct)
    {
        await _throttle.WaitAsync(ct).ConfigureAwait(false);
        return await _session.SendAsync(new MessageOptions { Prompt = prompt }, ct).ConfigureAwait(false);
    }

    public IDisposable On(Action<object> handler)
    {
        // Wrap the weakly-typed Action<object> in the SDK's SessionEventHandler delegate.
        // Pattern-matching on concrete SDK event types happens in the caller's lambda.
        SessionEventHandler bridge = evt => handler(evt);
        return _session.On(bridge);
    }

    public async ValueTask DisposeAsync()
    {
        var sessionId = _session.SessionId;
        await _session.DisposeAsync().ConfigureAwait(false);

        // Delete the session from the CLI process so it does not appear
        // in the VS Code Copilot chat history panel.
        try
        {
            await _client.DeleteSessionAsync(sessionId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to delete Copilot session {SessionId} — it may still appear in history", sessionId);
        }
    }
}
