using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Core.Services.AgentInvocation;
using REBUSS.Pure.Core.Services.CopilotReview;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Properties;
using REBUSS.Pure.Services.PrEnrichment;
using REBUSS.Pure.Tools.Shared;

namespace REBUSS.Pure.Tools
{
    /// <summary>
    /// Handles the execution of the get_pr_content MCP tool. Triggers an
    /// AI-assisted review of the PR's enriched content (via the configured agent —
    /// Copilot or Claude) and returns page review summaries.
    /// </summary>
    [McpServerToolType]
    public class GetPullRequestContentToolHandler
    {
        private readonly IPullRequestDataProvider _metadataProvider;
        private readonly IContextBudgetResolver _budgetResolver;
        private readonly IPrEnrichmentOrchestrator _enrichmentOrchestrator;
        private readonly IOptions<WorkflowOptions> _workflowOptions;
        private readonly ICopilotAvailabilityDetector _copilotAvailability;
        private readonly IAgentReviewOrchestrator _copilotReviewOrchestrator;
        private readonly Services.CopilotReview.AgentReviewWaiter _copilotReviewWaiter;
        private readonly IProgressReporter _progressReporter;
        private readonly AgentIdentity _agentIdentity;
        private readonly ILogger<GetPullRequestContentToolHandler> _logger;

        public GetPullRequestContentToolHandler(
            IPullRequestDataProvider metadataProvider,
            IContextBudgetResolver budgetResolver,
            IPrEnrichmentOrchestrator enrichmentOrchestrator,
            IOptions<WorkflowOptions> workflowOptions,
            ICopilotAvailabilityDetector copilotAvailability,
            IAgentReviewOrchestrator copilotReviewOrchestrator,
            Services.CopilotReview.AgentReviewWaiter copilotReviewWaiter,
            IProgressReporter progressReporter,
            AgentIdentity agentIdentity,
            ILogger<GetPullRequestContentToolHandler> logger)
        {
            _metadataProvider = metadataProvider;
            _budgetResolver = budgetResolver;
            _enrichmentOrchestrator = enrichmentOrchestrator;
            _workflowOptions = workflowOptions;
            _copilotAvailability = copilotAvailability;
            _copilotReviewOrchestrator = copilotReviewOrchestrator;
            _copilotReviewWaiter = copilotReviewWaiter;
            _progressReporter = progressReporter;
            _agentIdentity = agentIdentity;
            _logger = logger;
        }

        [McpServerTool(Name = "get_pr_content"), Description(
            "Returns AI-assisted review summaries for a pull request. " +
            "Reuses background enrichment from a prior get_pr_metadata call when available. " +
            "If enrichment is still running and cannot complete within the internal timeout, " +
            "returns a friendly 'still preparing' status block — never a raw timeout error.")]
        public async Task<IEnumerable<ContentBlock>> ExecuteAsync(
            [Description("The Pull Request number/ID")] int? prNumber = null,
            [Description("Page number (accepted for compatibility, ignored — all pages returned)")] int? pageNumber = null,
            [Description("Model name for context budget resolution")] string? modelName = null,
            [Description("Explicit token budget override")] int? maxTokens = null,
            IProgress<ProgressNotificationValue>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (prNumber == null)
                throw new McpException(Resources.ErrorMissingRequiredPrNumber);
            if (prNumber <= 0)
                throw new McpException(Resources.ErrorPrNumberMustBePositive);

            try
            {
                _logger.LogInformation(Resources.LogGetPrContentEntry, prNumber, pageNumber);
                var sw = Stopwatch.StartNew();

                await _progressReporter.ReportAsync(progress, 0, 4,
                    $"Starting content retrieval for PR #{prNumber}", cancellationToken);

                var budget = _budgetResolver.Resolve(maxTokens, modelName);
                var safeBudget = budget.SafeBudgetTokens;

                // Fast-path: a Failed snapshot means the prior background body raised.
                var snapshot = _enrichmentOrchestrator.TryGetSnapshot(prNumber.Value);
                if (snapshot is { Status: PrEnrichmentStatus.Failed, Failure: not null })
                {
                    _logger.LogInformation("PR {Pr} content request hit Failed snapshot; returning friendly status", prNumber);
                    return BuildFriendlyFailureBlocks(prNumber.Value, snapshot.Failure);
                }

                if (snapshot is null)
                {
                    var meta = await _metadataProvider.GetMetadataAsync(prNumber.Value, cancellationToken);
                    var headSha = meta.LastMergeSourceCommitId ?? string.Empty;
                    _enrichmentOrchestrator.TriggerEnrichment(prNumber.Value, headSha, safeBudget);
                }

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                linkedCts.CancelAfter(_workflowOptions.Value.ContentInternalTimeoutMs);

                await _progressReporter.ReportAsync(progress, 1, 4,
                    $"Waiting for enrichment — PR #{prNumber}", cancellationToken);

                PrEnrichmentResult result;
                try
                {
                    result = await _enrichmentOrchestrator.WaitForEnrichmentAsync(prNumber.Value, linkedCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation(
                        "PR {Pr} content wait expired after {TimeoutMs}ms; returning friendly status",
                        prNumber, _workflowOptions.Value.ContentInternalTimeoutMs);
                    return BuildFriendlyStillPreparingBlocks(prNumber.Value);
                }

                await _progressReporter.ReportAsync(progress, 2, null,
                    "Enrichment complete — checking review mode", cancellationToken);

                bool copilotAvailable;
                try
                {
                    copilotAvailable = await _copilotAvailability.IsAvailableAsync(cancellationToken);
                }
                catch (Services.CopilotReview.CopilotUnavailableException ex)
                {
                    sw.Stop();
                    _logger.LogWarning(
                        "PR {Pr} Copilot review layer unavailable (strict mode): {Reason}. Remediation: {Remediation}",
                        prNumber, ex.Verdict.Reason, ex.Verdict.Remediation);
                    throw;
                }

                if (!copilotAvailable)
                {
                    var verdict = await _copilotAvailability.GetVerdictAsync(cancellationToken);
                    throw new McpException(Services.CopilotReview.CopilotUnavailableMessage.Format(verdict));
                }

                await _progressReporter.ReportAsync(progress, 3, null,
                    $"AI review started for PR #{prNumber}", cancellationToken);

                var reviewKey = $"pr:{prNumber.Value}";
                _copilotReviewOrchestrator.TriggerReview(reviewKey, result);

                var reviewResult = await _copilotReviewWaiter.WaitWithProgressAsync(
                    reviewKey, progress, 4, cancellationToken);

                var blocks = BuildAgentAssistedBlocks(prNumber.Value, reviewResult, _agentIdentity.Name);

                sw.Stop();
                _logger.LogInformation(
                    "PR {Pr} agent-assisted content returned in {Ms}ms (agent '{Agent}', {Pages} pages, {Succeeded} ok, {Failed} failed)",
                    prNumber, sw.ElapsedMilliseconds, _agentIdentity.Name,
                    reviewResult.TotalPages, reviewResult.SucceededPages, reviewResult.FailedPages);

                return blocks;
            }
            catch (PullRequestNotFoundException ex)
            {
                _logger.LogWarning(ex, Resources.LogGetPrContentPrNotFound, prNumber);
                throw new McpException(string.Format(Resources.ErrorPullRequestNotFound, ex.Message));
            }
            catch (McpException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, Resources.LogGetPrContentError, prNumber, pageNumber);
                throw new McpException(string.Format(Resources.ErrorRetrievingPrContent, ex.Message));
            }
        }

        private static List<ContentBlock> BuildAgentAssistedBlocks(
            int prNumber, Core.Models.CopilotReview.AgentReviewResult reviewResult, string agentName)
        {
            var blocks = new List<ContentBlock>(reviewResult.PageReviews.Count + 1);

            blocks.Add(new TextContentBlock
            {
                Text = PlainTextFormatter.FormatAgentReviewHeader(
                    agentName,
                    prNumber,
                    reviewResult.TotalPages,
                    reviewResult.SucceededPages,
                    reviewResult.FailedPages)
            });

            foreach (var pageReview in reviewResult.PageReviews)
            {
                blocks.Add(new TextContentBlock
                {
                    Text = PlainTextFormatter.FormatAgentPageReviewBlock(pageReview)
                });
            }

            return blocks;
        }

        private static List<ContentBlock> BuildFriendlyStillPreparingBlocks(int prNumber)
        {
            return
            [
                new TextContentBlock
                {
                    Text = PlainTextFormatter.FormatFriendlyStatus(
                        headline: "Response is still being prepared",
                        explanation: $"Background enrichment for PR #{prNumber} is still running.",
                        suggestedNextAction: "Retry get_pr_content in a moment")
                }
            ];
        }

        private static List<ContentBlock> BuildFriendlyFailureBlocks(int prNumber, PrEnrichmentFailure failure)
        {
            return
            [
                new TextContentBlock
                {
                    Text = PlainTextFormatter.FormatFriendlyStatus(
                        headline: $"Background enrichment failed for PR #{prNumber}",
                        explanation: $"{failure.ExceptionTypeName}: {failure.SanitizedMessage}",
                        suggestedNextAction: "Retry get_pr_content")
                }
            ];
        }
    }
}
