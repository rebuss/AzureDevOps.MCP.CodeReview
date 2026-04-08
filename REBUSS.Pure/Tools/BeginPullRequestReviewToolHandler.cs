using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Services.PrEnrichment;
using REBUSS.Pure.Services.ResponsePacking;
using REBUSS.Pure.Services.ReviewSession;
using REBUSS.Pure.Tools.Shared;
using RSession = REBUSS.Pure.Services.ReviewSession.ReviewSession;

namespace REBUSS.Pure.Tools;

/// <summary>
/// Begins a stateful review session for a PR. See spec 012 FR-001 / FR-005 / FR-020 / FR-021.
/// </summary>
[McpServerToolType]
public class BeginPullRequestReviewToolHandler
{
    private readonly IPullRequestDataProvider _metadataProvider;
    private readonly IContextBudgetResolver _budgetResolver;
    private readonly IPrEnrichmentOrchestrator _enrichmentOrchestrator;
    private readonly IReviewSessionStore _sessionStore;
    private readonly IOptions<WorkflowOptions> _workflowOptions;
    private readonly ILogger<BeginPullRequestReviewToolHandler> _logger;

    public BeginPullRequestReviewToolHandler(
        IPullRequestDataProvider metadataProvider,
        IContextBudgetResolver budgetResolver,
        IPrEnrichmentOrchestrator enrichmentOrchestrator,
        IReviewSessionStore sessionStore,
        IOptions<WorkflowOptions> workflowOptions,
        ILogger<BeginPullRequestReviewToolHandler> logger)
    {
        _metadataProvider = metadataProvider;
        _budgetResolver = budgetResolver;
        _enrichmentOrchestrator = enrichmentOrchestrator;
        _sessionStore = sessionStore;
        _workflowOptions = workflowOptions;
        _logger = logger;
    }

    [McpServerTool(Name = "begin_pr_review"), Description(
        "Begins a stateful per-PR review session. Returns a session id and a manifest of every file " +
        "to review (alphabetically ordered). Subsequent calls go to next_review_item, " +
        "record_review_observation, and submit_pr_review. Sessions live only in process memory " +
        "and are lost on server restart.")]
    public async Task<IEnumerable<ContentBlock>> ExecuteAsync(
        [Description("The Pull Request number/ID")] int? prNumber = null,
        [Description("Model name for context budget resolution")] string? modelName = null,
        [Description("Explicit token budget override")] int? maxTokens = null,
        CancellationToken cancellationToken = default)
    {
        if (prNumber == null)
            throw new McpException("prNumber is required.");
        if (prNumber <= 0)
            throw new McpException("prNumber must be a positive integer.");

        try
        {
            _logger.LogInformation("begin_pr_review entry: pr={Pr}", prNumber);
            var budget = _budgetResolver.Resolve(maxTokens, modelName);
            var safeBudget = budget.SafeBudgetTokens;

            var snapshot = _enrichmentOrchestrator.TryGetSnapshot(prNumber.Value);
            string headSha;
            if (snapshot is null)
            {
                var meta = await _metadataProvider.GetMetadataAsync(prNumber.Value, cancellationToken);
                headSha = meta.LastMergeSourceCommitId ?? string.Empty;
                _enrichmentOrchestrator.TriggerEnrichment(prNumber.Value, headSha, safeBudget);
            }
            else
            {
                headSha = snapshot.HeadSha ?? string.Empty;
                if (snapshot.Status != PrEnrichmentStatus.Ready)
                    _enrichmentOrchestrator.TriggerEnrichment(prNumber.Value, headSha, safeBudget);
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.CancelAfter(_workflowOptions.Value.MetadataInternalTimeoutMs);

            PrEnrichmentResult result;
            try
            {
                result = await _enrichmentOrchestrator.WaitForEnrichmentAsync(prNumber.Value, linkedCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("begin_pr_review: enrichment not ready within timeout for pr {Pr}", prNumber);
                return new[]
                {
                    new TextContentBlock
                    {
                        Text = PlainTextFormatter.FormatFriendlyStatus(
                            headline: "PR enrichment still preparing",
                            explanation: $"Background enrichment for PR #{prNumber} has not completed yet.",
                            suggestedNextAction: "Call begin_pr_review again in a moment.")
                    }
                };
            }

            // Sort alphabetically by Path (FR-005). Reuse PackingCandidate fields.
            var files = result.SortedCandidates
                .OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
                .Select(c => new ReviewFileEntry(c.Path, c.Category, c.EstimatedTokens))
                .ToList();

            var sessionId = Guid.NewGuid().ToString("N");
            var session = new RSession(
                sessionId,
                prNumber.Value,
                result.HeadSha,
                safeBudget,
                files,
                result.EnrichedByPath,
                DateTimeOffset.UtcNow);
            _sessionStore.Add(session);

            var manifest = PlainTextFormatter.FormatSessionManifest(sessionId, prNumber.Value, safeBudget, files);
            _logger.LogInformation("begin_pr_review: session {Sid} created with {N} files", sessionId, files.Count);
            return new[] { new TextContentBlock { Text = manifest } };
        }
        catch (PullRequestNotFoundException ex)
        {
            throw new McpException($"Pull request not found: {ex.Message}");
        }
        catch (McpException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "begin_pr_review error for pr {Pr}", prNumber);
            throw new McpException($"Error beginning review session: {ex.Message}");
        }
    }
}
