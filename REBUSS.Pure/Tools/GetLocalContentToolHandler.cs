using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Services.CopilotReview;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Properties;
using REBUSS.Pure.Services.CopilotReview;
using REBUSS.Pure.Services.LocalReview;
using REBUSS.Pure.Services.PrEnrichment;
using REBUSS.Pure.Tools.Shared;

namespace REBUSS.Pure.Tools
{
    /// <summary>
    /// Handles the execution of the get_local_content MCP tool. Triggers Copilot
    /// review of enriched local changes and returns page review summaries.
    /// Copilot SDK is required — there is no content-only fallback.
    /// </summary>
    [McpServerToolType]
    public class GetLocalContentToolHandler
    {
        private readonly IContextBudgetResolver _budgetResolver;
        private readonly ILocalEnrichmentOrchestrator _enrichmentOrchestrator;
        private readonly IOptions<WorkflowOptions> _workflowOptions;
        private readonly ICopilotAvailabilityDetector _copilotAvailability;
        private readonly ICopilotReviewOrchestrator _copilotReviewOrchestrator;
        private readonly CopilotReviewWaiter _copilotReviewWaiter;
        private readonly IProgressReporter _progressReporter;
        private readonly ILogger<GetLocalContentToolHandler> _logger;

        public GetLocalContentToolHandler(
            IContextBudgetResolver budgetResolver,
            ILocalEnrichmentOrchestrator enrichmentOrchestrator,
            IOptions<WorkflowOptions> workflowOptions,
            ICopilotAvailabilityDetector copilotAvailability,
            ICopilotReviewOrchestrator copilotReviewOrchestrator,
            CopilotReviewWaiter copilotReviewWaiter,
            IProgressReporter progressReporter,
            ILogger<GetLocalContentToolHandler> logger)
        {
            _budgetResolver = budgetResolver;
            _enrichmentOrchestrator = enrichmentOrchestrator;
            _workflowOptions = workflowOptions;
            _copilotAvailability = copilotAvailability;
            _copilotReviewOrchestrator = copilotReviewOrchestrator;
            _copilotReviewWaiter = copilotReviewWaiter;
            _progressReporter = progressReporter;
            _logger = logger;
        }

        [McpServerTool(Name = "get_local_content"), Description(
            "Returns Copilot-assisted review summaries for local uncommitted changes. " +
            "Reuses background enrichment when available. If enrichment is still running " +
            "and cannot complete within the internal timeout, returns a friendly 'still " +
            "preparing' status block — never a raw timeout error.")]
        public async Task<IEnumerable<ContentBlock>> ExecuteAsync(
            [Description("Page number (accepted for compatibility, ignored — all pages returned)")] int? pageNumber = null,
            [Description("Review scope: 'working-tree' (default), 'staged', or a branch/ref name")] string? scope = null,
            [Description("Model name for context budget resolution")] string? modelName = null,
            [Description("Explicit token budget override")] int? maxTokens = null,
            IProgress<ProgressNotificationValue>? progress = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var parsedScope = LocalReviewScope.Parse(scope);
                var scopeString = parsedScope.ToString();
                _logger.LogInformation(Resources.LogGetLocalContentEntry, pageNumber, scopeString);
                var sw = Stopwatch.StartNew();

                await _progressReporter.ReportAsync(progress, 0, 4,
                    $"Starting content retrieval for local changes (scope: {scopeString})", cancellationToken);

                var budget = _budgetResolver.Resolve(maxTokens, modelName);
                var safeBudget = budget.SafeBudgetTokens;

                // Fast-path: a Failed snapshot means the prior background body raised.
                var snapshot = _enrichmentOrchestrator.TryGetSnapshot(scopeString);
                if (snapshot is { Status: LocalEnrichmentStatus.Failed, Failure: not null })
                {
                    _logger.LogInformation("Local content request hit Failed snapshot (scope '{Scope}'); returning friendly status", scopeString);
                    return BuildFriendlyFailureBlocks(scopeString, snapshot.Failure!);
                }

                _enrichmentOrchestrator.TriggerEnrichment(scopeString, safeBudget);

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                linkedCts.CancelAfter(_workflowOptions.Value.ContentInternalTimeoutMs);

                await _progressReporter.ReportAsync(progress, 1, 4,
                    $"Waiting for enrichment — local changes ({scopeString})", cancellationToken);

                LocalEnrichmentResult result;
                try
                {
                    result = await _enrichmentOrchestrator.WaitForEnrichmentAsync(scopeString, linkedCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation(
                        "Local content wait expired after {TimeoutMs}ms (scope '{Scope}'); returning friendly status",
                        _workflowOptions.Value.ContentInternalTimeoutMs, scopeString);
                    return BuildFriendlyStillPreparingBlocks(scopeString);
                }

                await _progressReporter.ReportAsync(progress, 2, null,
                    "Enrichment complete — checking review mode", cancellationToken);

                bool copilotAvailable;
                try
                {
                    copilotAvailable = await _copilotAvailability.IsAvailableAsync(cancellationToken);
                }
                catch (CopilotUnavailableException ex)
                {
                    sw.Stop();
                    _logger.LogWarning(
                        "Local review Copilot layer unavailable (strict mode): {Reason}. Remediation: {Remediation}",
                        ex.Verdict.Reason, ex.Verdict.Remediation);
                    throw;
                }

                if (!copilotAvailable)
                {
                    var verdict = await _copilotAvailability.GetVerdictAsync(cancellationToken);
                    throw new McpException(Services.CopilotReview.CopilotUnavailableMessage.Format(verdict));
                }

                await _progressReporter.ReportAsync(progress, 3, null,
                    $"Copilot review started for local changes ({scopeString})", cancellationToken);

                var reviewKey = $"local:{scopeString}:{result.RepositoryRoot}";
                _copilotReviewOrchestrator.TriggerReview(reviewKey, result);

                var copilotResult = await _copilotReviewWaiter.WaitWithProgressAsync(
                    reviewKey, progress, 4, cancellationToken);

                var copilotBlocks = BuildCopilotAssistedBlocks(scopeString, copilotResult);

                sw.Stop();
                _logger.LogInformation(
                    "Local copilot-assisted content returned in {Ms}ms (scope '{Scope}', {Pages} pages, {Succeeded} ok, {Failed} failed)",
                    sw.ElapsedMilliseconds, scopeString,
                    copilotResult.TotalPages, copilotResult.SucceededPages, copilotResult.FailedPages);

                return copilotBlocks;
            }
            catch (McpException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, Resources.LogGetLocalContentError, pageNumber, scope);
                throw new McpException(string.Format(Resources.ErrorRetrievingLocalContent, ex.Message));
            }
        }

        private static List<ContentBlock> BuildCopilotAssistedBlocks(
            string scopeString, Core.Models.CopilotReview.CopilotReviewResult copilotResult)
        {
            var blocks = new List<ContentBlock>(copilotResult.PageReviews.Count + 1);

            blocks.Add(new TextContentBlock
            {
                Text = PlainTextFormatter.FormatCopilotReviewHeader(
                    $"Local changes ({scopeString})",
                    copilotResult.TotalPages,
                    copilotResult.SucceededPages,
                    copilotResult.FailedPages)
            });

            foreach (var pageReview in copilotResult.PageReviews)
            {
                blocks.Add(new TextContentBlock
                {
                    Text = PlainTextFormatter.FormatCopilotPageReviewBlock(pageReview)
                });
            }

            return blocks;
        }

        private static List<ContentBlock> BuildFriendlyStillPreparingBlocks(string scopeString)
        {
            return
            [
                new TextContentBlock
                {
                    Text = PlainTextFormatter.FormatFriendlyStatus(
                        headline: "Response is still being prepared",
                        explanation: $"Background enrichment for local changes ({scopeString}) is still running.",
                        suggestedNextAction: "Retry get_local_content in a moment")
                }
            ];
        }

        private static List<ContentBlock> BuildFriendlyFailureBlocks(string scopeString, LocalEnrichmentFailure failure)
        {
            return
            [
                new TextContentBlock
                {
                    Text = PlainTextFormatter.FormatFriendlyStatus(
                        headline: $"Background enrichment failed for local changes ({scopeString})",
                        explanation: $"{failure.ExceptionTypeName}: {failure.SanitizedMessage}",
                        suggestedNextAction: "Retry get_local_content")
                }
            ];
        }
    }
}
