using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using REBUSS.Pure.Core.Models.CopilotReview;
using REBUSS.Pure.Core.Services.AgentInvocation;
using REBUSS.Pure.Core.Services.CopilotReview;
using REBUSS.Pure.Services.CopilotReview.Inspection;

namespace REBUSS.Pure.Services.CopilotReview;

/// <summary>
/// Agent-backed page reviewer. Sends one page of enriched content through
/// <see cref="IAgentInvoker"/> and returns a <see cref="AgentPageReviewResult"/>.
/// Session lifecycle, streaming accumulation, and throttling are the invoker's concern.
/// Never throws except on <see cref="OperationCanceledException"/>.
/// </summary>
internal sealed class AgentPageReviewer : IAgentPageReviewer
{
    private readonly IAgentInvoker _agentInvoker;
    private readonly IOptions<CopilotReviewOptions> _options;
    private readonly ILogger<AgentPageReviewer> _logger;
    private readonly IAgentInspectionWriter _inspection;

    public AgentPageReviewer(
        IAgentInvoker agentInvoker,
        IOptions<CopilotReviewOptions> options,
        ILogger<AgentPageReviewer> logger,
        IAgentInspectionWriter inspection)
    {
        _agentInvoker = agentInvoker;
        _options = options;
        _logger = logger;
        _inspection = inspection;
    }

    private const string PromptResourceName = "REBUSS.Pure.Services.CopilotReview.Prompts.copilot-page-review.md";
    private const string PromptResourceNameUnderscore = "REBUSS.Pure.Services.CopilotReview.Prompts.copilot_page_review.md";
    private static string? _cachedPromptTemplate;

    public async Task<AgentPageReviewResult> ReviewPageAsync(
        string reviewKey,
        int pageNumber,
        string enrichedPageContent,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reviewKey);
        ArgumentNullException.ThrowIfNull(enrichedPageContent);

        // Defence-in-depth: never dispatch an empty diff to the agent SDK. With no diff
        // text below the review-instruction template, the LLM treats the template as the
        // entire input and hallucinates a refusal (commonly the "MCP tool not available"
        // pattern). Upstream short-circuits should make this unreachable, but if they
        // are bypassed (custom orchestrator, future code path), fail loudly here.
        if (string.IsNullOrWhiteSpace(enrichedPageContent))
        {
            _logger.LogWarning(
                "Refusing to dispatch empty enriched content for review key '{ReviewKey}' page {Page}",
                reviewKey, pageNumber);
            return AgentPageReviewResult.Failure(
                pageNumber,
                Array.Empty<string>(),
                "Empty enriched page content — refusing to dispatch to agent SDK to avoid hallucinated refusals.",
                attemptsMade: 1);
        }

        string prompt;
        try
        {
            prompt = LoadPromptTemplate()
                .Replace("{enrichedPageContent}", enrichedPageContent);
        }
        catch (Exception ex)
        {
            return AgentPageReviewResult.Failure(
                pageNumber, Array.Empty<string>(), $"prompt template load failed: {ex.Message}", 1);
        }

        // Feature 022: capture the prompt that will be sent to the agent. The inspection
        // writer is a NoOp when the env var is unset, so this is effectively free when
        // disabled.
        //
        // ORDERING (load-bearing): WritePromptAsync runs BEFORE InvokeAsync. The
        // failure-path test AgentPageReviewerTests.ReviewPage_FailurePath_OnlyPromptIsCaptured
        // pins this — when the invoker throws, the prompt is still captured for diagnosis
        // while the response is not. Reordering the prompt write into the success branch
        // (or after the invoke) would silently break post-mortem diagnosis of failed pages.
        var inspectionKind = $"page-{pageNumber}-review";
        await _inspection.WritePromptAsync(reviewKey, inspectionKind, prompt, ct).ConfigureAwait(false);

        try
        {
            var reviewText = await _agentInvoker
                .InvokeAsync(prompt, _options.Value.Model, ct)
                .ConfigureAwait(false);

            // Feature 022: capture the response only on the happy path — failure paths
            // fall to the catch blocks below.
            await _inspection.WriteResponseAsync(reviewKey, inspectionKind, reviewText, ct).ConfigureAwait(false);

            return AgentPageReviewResult.Success(pageNumber, reviewText, attemptsMade: 1);
        }
        catch (OperationCanceledException)
        {
            throw; // cancellation propagates to the orchestrator
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Copilot page {PageNumber} review attempt failed", pageNumber);
            return AgentPageReviewResult.Failure(
                pageNumber, Array.Empty<string>(), ex.Message, 1);
        }
    }

    private static string LoadPromptTemplate()
    {
        return LazyInitializer.EnsureInitialized(ref _cachedPromptTemplate, () =>
        {
            var assembly = typeof(AgentPageReviewer).Assembly;
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
