using System.Reflection;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using REBUSS.Pure.Core.Models.CopilotReview;
using REBUSS.Pure.Core.Services.CopilotReview;

namespace REBUSS.Pure.Services.CopilotReview;

/// <summary>
/// SDK-backed page reviewer. Creates a single <c>CopilotSession</c> per call via
/// <see cref="ICopilotSessionFactory"/>, sends the prompt, and collects the response
/// through event pattern-matching on <c>AssistantMessageEvent</c> / <c>SessionIdleEvent</c>
/// / <c>SessionErrorEvent</c>. Never throws except on <see cref="OperationCanceledException"/>.
/// Per research.md Decision 1.
/// <para>
/// Phase 2 skeleton — real implementation arrives in T019 (US1 Phase 3).
/// </para>
/// </summary>
internal sealed class CopilotPageReviewer : ICopilotPageReviewer
{
    private readonly ICopilotSessionFactory _sessionFactory;
    private readonly IOptions<CopilotReviewOptions> _options;
    private readonly ILogger<CopilotPageReviewer> _logger;

    public CopilotPageReviewer(
        ICopilotSessionFactory sessionFactory,
        IOptions<CopilotReviewOptions> options,
        ILogger<CopilotPageReviewer> logger)
    {
        _sessionFactory = sessionFactory;
        _options = options;
        _logger = logger;
    }

    private const string PromptResourceName = "REBUSS.Pure.Services.CopilotReview.Prompts.copilot-page-review.md";
    private const string PromptResourceNameUnderscore = "REBUSS.Pure.Services.CopilotReview.Prompts.copilot_page_review.md";
    private static string? _cachedPromptTemplate;

    public async Task<CopilotPageReviewResult> ReviewPageAsync(
        int pageNumber,
        string enrichedPageContent,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(enrichedPageContent);

        string prompt;
        try
        {
            prompt = (await LoadPromptTemplateAsync(ct).ConfigureAwait(false))
                .Replace("{enrichedPageContent}", enrichedPageContent);
        }
        catch (Exception ex)
        {
            return CopilotPageReviewResult.Failure(
                pageNumber, Array.Empty<string>(), $"prompt template load failed: {ex.Message}", 1);
        }

        ICopilotSessionHandle? handle = null;
        try
        {
            handle = await _sessionFactory.CreateSessionAsync(_options.Value.Model, ct).ConfigureAwait(false);

            // Event-driven response collection:
            //   AssistantMessageEvent → capture Content
            //   SessionIdleEvent      → complete the TCS (with captured content, or empty → failure)
            //   SessionErrorEvent     → complete the TCS with an exception
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            string? capturedContent = null;

            using var subscription = handle.On(evt =>
            {
                switch (evt)
                {
                    case AssistantMessageEvent msg:
                        capturedContent = msg.Data?.Content;
                        break;
                    case SessionErrorEvent err:
                        tcs.TrySetException(new InvalidOperationException(
                            err.Data?.Message ?? "session error (no message)"));
                        break;
                    case SessionIdleEvent:
                        if (!string.IsNullOrWhiteSpace(capturedContent))
                            tcs.TrySetResult(capturedContent!);
                        else
                            tcs.TrySetException(new InvalidOperationException(
                                "session idle without assistant message content"));
                        break;
                }
            });

            await handle.SendAsync(prompt, ct).ConfigureAwait(false);
            var reviewText = await tcs.Task.WaitAsync(ct).ConfigureAwait(false);

            return CopilotPageReviewResult.Success(pageNumber, reviewText, attemptsMade: 1);
        }
        catch (OperationCanceledException)
        {
            throw; // cancellation propagates to the orchestrator
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Copilot page {PageNumber} review attempt failed", pageNumber);
            return CopilotPageReviewResult.Failure(
                pageNumber, Array.Empty<string>(), ex.Message, 1);
        }
        finally
        {
            if (handle is not null)
            {
                try { await handle.DisposeAsync().ConfigureAwait(false); } catch { /* swallow */ }
            }
        }
    }

    private static async Task<string> LoadPromptTemplateAsync(CancellationToken ct)
    {
        if (_cachedPromptTemplate is not null)
            return _cachedPromptTemplate;

        var assembly = typeof(CopilotPageReviewer).Assembly;
        var stream = assembly.GetManifestResourceStream(PromptResourceName)
            ?? assembly.GetManifestResourceStream(PromptResourceNameUnderscore);
        if (stream is null)
        {
            // Fallback: search by suffix in case the SDK mangles file naming.
            var match = Array.Find(
                assembly.GetManifestResourceNames(),
                n => n.EndsWith("copilot-page-review.md", StringComparison.OrdinalIgnoreCase)
                  || n.EndsWith("copilot_page_review.md", StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                stream = assembly.GetManifestResourceStream(match);
        }
        if (stream is null)
            throw new FileNotFoundException(
                "Embedded resource 'copilot-page-review.md' not found in REBUSS.Pure assembly.");

        using (stream)
        using (var reader = new StreamReader(stream))
        {
            _cachedPromptTemplate = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }
        return _cachedPromptTemplate;
    }
}
