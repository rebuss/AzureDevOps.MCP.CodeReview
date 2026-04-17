using System.Reflection;
using System.Text;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using REBUSS.Pure.Core.Models.CopilotReview;
using REBUSS.Pure.Core.Services.CopilotReview;
using REBUSS.Pure.Services.CopilotReview.Inspection;

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
    private readonly ICopilotInspectionWriter _inspection;

    public CopilotPageReviewer(
        ICopilotSessionFactory sessionFactory,
        IOptions<CopilotReviewOptions> options,
        ILogger<CopilotPageReviewer> logger,
        ICopilotInspectionWriter inspection)
    {
        _sessionFactory = sessionFactory;
        _options = options;
        _logger = logger;
        _inspection = inspection;
    }

    private const string PromptResourceName = "REBUSS.Pure.Services.CopilotReview.Prompts.copilot-page-review.md";
    private const string PromptResourceNameUnderscore = "REBUSS.Pure.Services.CopilotReview.Prompts.copilot_page_review.md";
    private static string? _cachedPromptTemplate;

    public async Task<CopilotPageReviewResult> ReviewPageAsync(
        string reviewKey,
        int pageNumber,
        string enrichedPageContent,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reviewKey);
        ArgumentNullException.ThrowIfNull(enrichedPageContent);

        string prompt;
        try
        {
            prompt = LoadPromptTemplate()
                .Replace("{enrichedPageContent}", enrichedPageContent);
        }
        catch (Exception ex)
        {
            return CopilotPageReviewResult.Failure(
                pageNumber, Array.Empty<string>(), $"prompt template load failed: {ex.Message}", 1);
        }

        // Feature 022: capture the prompt that will be sent to Copilot. Fires only on the
        // happy path — the inspection writer is a NoOp when the env var is unset, so this
        // is effectively free when disabled.
        var inspectionKind = $"page-{pageNumber}-review";
        await _inspection.WritePromptAsync(reviewKey, inspectionKind, prompt, ct).ConfigureAwait(false);

        ICopilotSessionHandle? handle = null;
        try
        {
            handle = await _sessionFactory.CreateSessionAsync(_options.Value.Model, ct).ConfigureAwait(false);

            // Event-driven response collection:
            //   AssistantMessageEvent → accumulate Content (phased-output models emit
            //                           multiple events per session — thinking + response)
            //   SessionIdleEvent      → complete the TCS with the accumulated content,
            //                           or with an error when no content was captured
            //   SessionErrorEvent     → complete the TCS with an exception
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var contentBuilder = new StringBuilder();

            using var subscription = handle.On(evt =>
            {
                switch (evt)
                {
                    case AssistantMessageEvent msg:
                        // Guard against null only — `StringBuilder.Append(string?)` with null
                        // is a documented no-op, but empty fragments are legitimate stream
                        // chunks from phased-output models and must be appended (no-op is
                        // fine; silently dropping them is not, because any filtering
                        // obscures what the model actually emitted).
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

            await handle.SendAsync(prompt, ct).ConfigureAwait(false);
            var reviewText = await tcs.Task.WaitAsync(ct).ConfigureAwait(false);

            // Feature 022: capture the response. Only on the happy path — failure paths
            // (session error, empty response) fall to the catch blocks below.
            await _inspection.WriteResponseAsync(reviewKey, inspectionKind, reviewText, ct).ConfigureAwait(false);

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

    private static string LoadPromptTemplate()
    {
        return LazyInitializer.EnsureInitialized(ref _cachedPromptTemplate, () =>
        {
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
                return reader.ReadToEnd();
            }
        })!;
    }
}
