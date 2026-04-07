using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Properties;
using REBUSS.Pure.Services.PrEnrichment;
using REBUSS.Pure.Tools.Shared;

namespace REBUSS.Pure.Tools
{
    /// <summary>
    /// Handles the execution of the get_pr_content MCP tool. Serves a single
    /// page of enriched PR diff content from the
    /// <see cref="IPrEnrichmentOrchestrator"/>'s cached result, kicking off
    /// background enrichment as a fallback when no prior <c>get_pr_metadata</c>
    /// call has primed it. Runs with its own fresh internal timeout window;
    /// on timeout returns a friendly "still preparing" status block instead of
    /// surfacing a tool-call failure.
    /// </summary>
    [McpServerToolType]
    public class GetPullRequestContentToolHandler
    {
        private readonly IPullRequestDataProvider _metadataProvider;
        private readonly IContextBudgetResolver _budgetResolver;
        private readonly IPrEnrichmentOrchestrator _enrichmentOrchestrator;
        private readonly IOptions<WorkflowOptions> _workflowOptions;
        private readonly ILogger<GetPullRequestContentToolHandler> _logger;

        public GetPullRequestContentToolHandler(
            IPullRequestDataProvider metadataProvider,
            IContextBudgetResolver budgetResolver,
            IPrEnrichmentOrchestrator enrichmentOrchestrator,
            IOptions<WorkflowOptions> workflowOptions,
            ILogger<GetPullRequestContentToolHandler> logger)
        {
            _metadataProvider = metadataProvider;
            _budgetResolver = budgetResolver;
            _enrichmentOrchestrator = enrichmentOrchestrator;
            _workflowOptions = workflowOptions;
            _logger = logger;
        }

        [McpServerTool(Name = "get_pr_content"), Description(
            "Returns plain-text diff content for a specific page of a pull request review. " +
            "One content block per file with -/+/space prefixed lines, plus a pagination footer. " +
            "Reuses background enrichment from a prior get_pr_metadata call when available. " +
            "If enrichment is still running and cannot complete within the internal timeout, " +
            "returns a friendly 'still preparing' status block — never a raw timeout error.")]
        public async Task<IEnumerable<ContentBlock>> ExecuteAsync(
            [Description("The Pull Request number/ID")] int? prNumber = null,
            [Description("Page number to retrieve (1-based)")] int? pageNumber = null,
            [Description("Model name for context budget resolution")] string? modelName = null,
            [Description("Explicit token budget override")] int? maxTokens = null,
            CancellationToken cancellationToken = default)
        {
            if (prNumber == null)
                throw new McpException(Resources.ErrorMissingRequiredPrNumber);
            if (prNumber <= 0)
                throw new McpException(Resources.ErrorPrNumberMustBePositive);
            if (pageNumber == null)
                throw new McpException(Resources.ErrorMissingRequiredPageNumber);
            if (pageNumber < 1)
                throw new McpException(Resources.ErrorPageNumberMustBePositive);

            try
            {
                _logger.LogInformation(Resources.LogGetPrContentEntry, prNumber, pageNumber);
                var sw = Stopwatch.StartNew();

                var budget = _budgetResolver.Resolve(maxTokens, modelName);
                var safeBudget = budget.SafeBudgetTokens;

                // Fast-path: a Failed snapshot means the prior background body raised.
                // Surface it via friendly-status without retriggering.
                var snapshot = _enrichmentOrchestrator.TryGetSnapshot(prNumber.Value);
                if (snapshot is { Status: PrEnrichmentStatus.Failed, Failure: not null })
                {
                    _logger.LogInformation("PR {Pr} content request hit Failed snapshot; returning friendly status", prNumber);
                    return BuildFriendlyFailureBlocks(prNumber.Value, pageNumber.Value, snapshot.Failure!);
                }

                // SHA discovery: if there is no existing job, ask the metadata provider for the head SHA
                // and start one. If a job exists with a stale safeBudget, supersede it with the new budget.
                if (snapshot is null)
                {
                    var meta = await _metadataProvider.GetMetadataAsync(prNumber.Value, cancellationToken);
                    var headSha = meta.LastMergeSourceCommitId ?? string.Empty;
                    _enrichmentOrchestrator.TriggerEnrichment(prNumber.Value, headSha, safeBudget);
                }
                else if (snapshot.Status == PrEnrichmentStatus.Ready
                         && snapshot.Result is { } existingResult
                         && existingResult.SafeBudgetTokens != safeBudget)
                {
                    _logger.LogInformation(
                        "PR {Pr} budget changed ({Old} -> {New}); retriggering enrichment",
                        prNumber, existingResult.SafeBudgetTokens, safeBudget);
                    _enrichmentOrchestrator.TriggerEnrichment(prNumber.Value, snapshot.HeadSha, safeBudget);
                }

                // Wait with our own internal timeout. The background body keeps running
                // even if this wait expires.
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                linkedCts.CancelAfter(_workflowOptions.Value.ContentInternalTimeoutMs);

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
                    return BuildFriendlyStillPreparingBlocks(prNumber.Value, pageNumber.Value);
                }

                if (pageNumber > result.Allocation.TotalPages)
                    throw new McpException(
                        string.Format(Resources.ErrorPageNumberExceedsTotalPages, pageNumber, result.Allocation.TotalPages));

                var pageSlice = result.Allocation.Pages[pageNumber.Value - 1];
                var blocks = BuildPageBlocks(pageSlice, pageNumber.Value, result);

                sw.Stop();
                _logger.LogInformation(Resources.LogGetPrContentCompleted,
                    prNumber, pageNumber, result.Allocation.TotalPages, pageSlice.Items.Count, sw.ElapsedMilliseconds);

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

        private static List<ContentBlock> BuildPageBlocks(
            Core.Models.Pagination.PageSlice pageSlice, int pageNumber, PrEnrichmentResult result)
        {
            var blocks = new List<ContentBlock>(pageSlice.Items.Count + 1);

            // Per-file enriched text, in the order produced by the page allocator.
            var pagePathsOrdered = new List<string>(pageSlice.Items.Count);
            foreach (var item in pageSlice.Items)
            {
                var path = result.SortedCandidates[item.OriginalIndex].Path;
                pagePathsOrdered.Add(path);
                if (result.EnrichedByPath.TryGetValue(path, out var enrichedText))
                    blocks.Add(new TextContentBlock { Text = enrichedText });
            }

            var categories = BuildCategoryBreakdown(pagePathsOrdered, result.SortedCandidates);

            blocks.Add(new TextContentBlock
            {
                Text = PlainTextFormatter.FormatSimplePaginationBlock(
                    pageNumber, result.Allocation.TotalPages,
                    pagePathsOrdered.Count, result.Allocation.TotalItems,
                    pageSlice.BudgetUsed,
                    categories)
            });

            return blocks;
        }

        private static List<ContentBlock> BuildFriendlyStillPreparingBlocks(int prNumber, int pageNumber)
        {
            return new List<ContentBlock>
            {
                new TextContentBlock
                {
                    Text = PlainTextFormatter.FormatFriendlyStatus(
                        headline: "Response is still being prepared",
                        explanation: $"Background enrichment for PR #{prNumber} is still running.",
                        suggestedNextAction: $"Retry get_pr_content with pageNumber={pageNumber} in a moment")
                }
            };
        }

        private static List<ContentBlock> BuildFriendlyFailureBlocks(int prNumber, int pageNumber, PrEnrichmentFailure failure)
        {
            return new List<ContentBlock>
            {
                new TextContentBlock
                {
                    Text = PlainTextFormatter.FormatFriendlyStatus(
                        headline: $"Background enrichment failed for PR #{prNumber}",
                        explanation: $"{failure.ExceptionTypeName}: {failure.SanitizedMessage}",
                        suggestedNextAction: $"Retry get_pr_content with pageNumber={pageNumber}")
                }
            };
        }

        private static Dictionary<string, int> BuildCategoryBreakdown(
            List<string> pagePaths, IReadOnlyList<PackingCandidate> sortedCandidates)
        {
            var categories = new Dictionary<string, int>();
            var pagePathSet = new HashSet<string>(pagePaths, StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in sortedCandidates)
            {
                if (!pagePathSet.Contains(candidate.Path)) continue;
                var key = candidate.Category.ToString().ToLowerInvariant();
                categories[key] = categories.GetValueOrDefault(key) + 1;
            }
            return categories;
        }
    }
}
