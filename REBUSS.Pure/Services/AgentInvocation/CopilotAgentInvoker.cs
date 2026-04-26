using System.Text;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using REBUSS.Pure.Core.Services.AgentInvocation;
using REBUSS.Pure.Core.Services.CopilotReview;
using REBUSS.Pure.Services.CopilotReview;

namespace REBUSS.Pure.Services.AgentInvocation;

/// <summary>
/// <see cref="IAgentInvoker"/> implementation that wraps
/// <see cref="ICopilotSessionFactory"/> — creates a fresh session per call,
/// subscribes to assistant-message/idle/error events, sends the prompt, and
/// returns the accumulated response text. Session lifetime is bounded to the call.
/// </summary>
/// <remarks>
/// Extracted from the previously inlined session+events block in
/// <c>AgentPageReviewer</c> / <c>FindingValidator</c> so the same one-shot
/// contract can be served by either GitHub Copilot or Claude Code CLI.
/// </remarks>
public sealed class CopilotAgentInvoker : IAgentInvoker
{
    private readonly ICopilotSessionFactory _sessionFactory;
    private readonly IOptions<CopilotReviewOptions> _options;
    private readonly ILogger<CopilotAgentInvoker>? _logger;

    public CopilotAgentInvoker(
        ICopilotSessionFactory sessionFactory,
        IOptions<CopilotReviewOptions> options,
        ILogger<CopilotAgentInvoker>? logger = null)
    {
        _sessionFactory = sessionFactory;
        _options = options;
        _logger = logger;
    }

    public async Task<string> InvokeAsync(string prompt, string? model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        // IAgentInvoker documents `model` as an optional hint. The Copilot SDK requires a
        // concrete model string, so fall back to the configured default
        // (CopilotReviewOptions.Model) when the caller leaves it null/empty. The
        // configuration default is non-empty out of the box; an empty fallback indicates
        // the operator explicitly cleared it and is a configuration error.
        var effectiveModel = string.IsNullOrWhiteSpace(model) ? _options.Value.Model : model;
        if (string.IsNullOrWhiteSpace(effectiveModel))
            throw new InvalidOperationException(
                "Copilot model is not configured: CopilotReviewOptions.Model is empty and no model was passed to InvokeAsync.");

        ICopilotSessionHandle? handle = null;
        try
        {
            handle = await _sessionFactory.CreateSessionAsync(effectiveModel, cancellationToken).ConfigureAwait(false);

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var contentBuilder = new StringBuilder();

            using var subscription = handle.On(evt =>
            {
                switch (evt)
                {
                    case AssistantMessageEvent msg:
                        // Phased-output models emit multiple chunks per session; accumulate
                        // every non-null fragment (empty string is a legitimate stream chunk).
                        var chunk = msg.Data?.Content;
                        if (chunk is not null)
                            contentBuilder.Append(chunk);
                        break;
                    case SessionErrorEvent err:
                        tcs.TrySetException(new InvalidOperationException(
                            err.Data?.Message ?? "session error (no message)"));
                        break;
                    case SessionIdleEvent:
                        var captured = contentBuilder.ToString();
                        if (!string.IsNullOrWhiteSpace(captured))
                            tcs.TrySetResult(captured);
                        else
                            tcs.TrySetException(new InvalidOperationException(
                                "session idle without assistant message content"));
                        break;
                }
            });

            await handle.SendAsync(prompt, cancellationToken).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (handle is not null)
            {
                try { await handle.DisposeAsync().ConfigureAwait(false); } catch { /* swallow */ }
            }
        }
    }
}
