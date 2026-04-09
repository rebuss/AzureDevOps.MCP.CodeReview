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
/// Phase 2 skeleton — the real body arrives in T019a (US1 Phase 3).
/// </para>
/// </summary>
internal sealed class CopilotSessionFactory : ICopilotSessionFactory
{
    private readonly ICopilotClientProvider _provider;
    private readonly IOptions<CopilotReviewOptions> _options;
    private readonly ILogger<CopilotSessionFactory> _logger;

    public CopilotSessionFactory(
        ICopilotClientProvider provider,
        IOptions<CopilotReviewOptions> options,
        ILogger<CopilotSessionFactory> logger)
    {
        _provider = provider;
        _options = options;
        _logger = logger;
    }

    public async Task<ICopilotSessionHandle> CreateSessionAsync(string model, CancellationToken ct)
    {
        var started = await _provider.TryEnsureStartedAsync(ct).ConfigureAwait(false);
        if (!started)
            throw new InvalidOperationException(
                "Copilot client is not started — CopilotAvailabilityDetector.IsAvailableAsync should gate this call.");

        var client = (CopilotClient)_provider.Client;

        var config = new SessionConfig
        {
            Model = model,
            OnPermissionRequest = PermissionHandler.ApproveAll,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
            Streaming = false,
        };

        var session = await client.CreateSessionAsync(config, ct).ConfigureAwait(false);
        return new CopilotSessionHandle(session);
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

    public CopilotSessionHandle(CopilotSession session)
    {
        _session = session;
    }

    public Task<string> SendAsync(string prompt, CancellationToken ct) =>
        _session.SendAsync(new MessageOptions { Prompt = prompt }, ct);

    public IDisposable On(Action<object> handler)
    {
        // Wrap the weakly-typed Action<object> in the SDK's SessionEventHandler delegate.
        // Pattern-matching on concrete SDK event types happens in the caller's lambda.
        SessionEventHandler bridge = evt => handler(evt);
        return _session.On(bridge);
    }

    public ValueTask DisposeAsync() => _session.DisposeAsync();
}
