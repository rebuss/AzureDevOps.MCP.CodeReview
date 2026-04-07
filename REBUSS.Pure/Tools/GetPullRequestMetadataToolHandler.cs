using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Properties;
using REBUSS.Pure.Services.PrEnrichment;
using REBUSS.Pure.Tools.Shared;

namespace REBUSS.Pure.Tools
{
    /// <summary>
    /// Handles the execution of the get_pr_metadata MCP tool.
    /// Validates input, delegates to <see cref="IPullRequestDataProvider"/>,
    /// and formats the result as a plain-text response.
    ///
    /// When budget parameters are provided, kicks off a background enrichment
    /// job via <see cref="IPrEnrichmentOrchestrator"/> and waits up to
    /// <c>WorkflowOptions.MetadataInternalTimeoutMs</c> for it to complete.
    /// On internal timeout, falls back to a basic-summary response with an
    /// explicit "paging not yet available" indicator (FR-002, FR-004) so the
    /// host never sees a tool-call timeout regardless of PR size.
    /// </summary>
    [McpServerToolType]
    public class GetPullRequestMetadataToolHandler
    {
        private readonly IPullRequestDataProvider _metadataProvider;
        private readonly IContextBudgetResolver _budgetResolver;
        private readonly IRepositoryDownloadOrchestrator _downloadOrchestrator;
        private readonly IPrEnrichmentOrchestrator _enrichmentOrchestrator;
        private readonly IOptions<WorkflowOptions> _workflowOptions;
        private readonly ILogger<GetPullRequestMetadataToolHandler> _logger;

        public GetPullRequestMetadataToolHandler(
            IPullRequestDataProvider metadataProvider,
            IContextBudgetResolver budgetResolver,
            IRepositoryDownloadOrchestrator downloadOrchestrator,
            IPrEnrichmentOrchestrator enrichmentOrchestrator,
            IOptions<WorkflowOptions> workflowOptions,
            ILogger<GetPullRequestMetadataToolHandler> logger)
        {
            _metadataProvider = metadataProvider;
            _budgetResolver = budgetResolver;
            _downloadOrchestrator = downloadOrchestrator;
            _enrichmentOrchestrator = enrichmentOrchestrator;
            _workflowOptions = workflowOptions;
            _logger = logger;
        }

        [McpServerTool(Name = "get_pr_metadata"), Description(
            "Retrieves metadata for a specific Pull Request. " +
            "Returns a plain-text block with PR details including title, author, state, " +
            "branches, stats, commit SHAs, and description. " +
            "When modelName or maxTokens is provided, also returns content paging info " +
            "with page count and per-page file breakdown for use with get_pr_content. " +
            "If background enrichment cannot complete within the internal timeout, the response " +
            "still returns the basic summary with an explicit 'paging not yet available' indicator " +
            "so the host never times out — call get_pr_content to retrieve the enriched content.")]
        public async Task<IEnumerable<ContentBlock>> ExecuteAsync(
            [Description("The Pull Request number/ID to retrieve metadata for")] int? prNumber = null,
            [Description("Model name for context budget resolution (e.g. 'gpt-4o'). Triggers pagination info.")] string? modelName = null,
            [Description("Explicit token budget override. Triggers pagination info.")] int? maxTokens = null,
            CancellationToken cancellationToken = default)
        {
            if (prNumber != null && prNumber <= 0)
                throw new McpException(Resources.ErrorPrNumberMustBePositive);

            if (prNumber == null)
                throw new McpException(Resources.ErrorMissingRequiredPrNumber);

            try
            {
                _logger.LogInformation(Resources.LogGetPrMetadataEntry, prNumber);
                var sw = Stopwatch.StartNew();

                var metadata = await _metadataProvider.GetMetadataAsync(prNumber.Value, cancellationToken);

                // Trigger background repository download of the base/target branch (fire-and-forget)
                var downloadCommitRef = !string.IsNullOrEmpty(metadata.LastMergeTargetCommitId)
                    ? metadata.LastMergeTargetCommitId
                    : metadata.LastMergeSourceCommitId;
                if (!string.IsNullOrEmpty(downloadCommitRef))
                    _downloadOrchestrator.TriggerDownloadAsync(prNumber.Value, downloadCommitRef);

                (int TotalPages, int TotalFiles, int BudgetPerPage, IReadOnlyList<(int Page, int Count)> ByPage)? paging = null;
                bool pagingDeferred = false;
                PrEnrichmentFailure? enrichmentFailure = null;

                if (modelName != null || maxTokens != null)
                {
                    var pagingResult = await TryBuildContentPagingAsync(
                        prNumber.Value, metadata.LastMergeSourceCommitId ?? string.Empty, modelName, maxTokens, cancellationToken);

                    paging = pagingResult.Paging;
                    pagingDeferred = pagingResult.Deferred;
                    enrichmentFailure = pagingResult.Failure;
                }

                var text = PlainTextFormatter.FormatMetadata(metadata, prNumber.Value, paging, pagingDeferred);
                sw.Stop();

                _logger.LogInformation(Resources.LogGetPrMetadataCompleted, prNumber, text.Length, sw.ElapsedMilliseconds);

                var blocks = new List<ContentBlock>(2) { new TextContentBlock { Text = text } };

                // FR-017: surface enrichment failures via the friendly-status pipeline.
                if (enrichmentFailure is not null)
                {
                    blocks.Add(new TextContentBlock
                    {
                        Text = PlainTextFormatter.FormatFriendlyStatus(
                            headline: $"Background enrichment failed for PR #{prNumber}",
                            explanation: $"{enrichmentFailure.ExceptionTypeName}: {enrichmentFailure.SanitizedMessage}",
                            suggestedNextAction: "Retry get_pr_metadata or call get_pr_content with pageNumber=1")
                    });
                }

                return blocks;
            }
            catch (PullRequestNotFoundException ex)
            {
                _logger.LogWarning(ex, Resources.LogGetPrMetadataNotFound, prNumber);
                throw new McpException(string.Format(Resources.ErrorPullRequestNotFound, ex.Message));
            }
            catch (McpException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, Resources.LogGetPrMetadataError, prNumber);
                throw new McpException(string.Format(Resources.ErrorRetrievingPrMetadata, ex.Message));
            }
        }

        private async Task<PagingFetchResult> TryBuildContentPagingAsync(
            int prNumber, string headSha, string? modelName, int? maxTokens, CancellationToken callerCt)
        {
            var budget = _budgetResolver.Resolve(maxTokens, modelName);
            var safeBudget = budget.SafeBudgetTokens;

            // Fast-path: if a prior call already produced a Failed snapshot for this PR,
            // surface the failure immediately without retriggering.
            var existing = _enrichmentOrchestrator.TryGetSnapshot(prNumber);
            if (existing is { Status: PrEnrichmentStatus.Failed, HeadSha: var sha } && sha == headSha)
            {
                return new PagingFetchResult(null, Deferred: false, Failure: existing.Failure);
            }

            _enrichmentOrchestrator.TriggerEnrichment(prNumber, headSha, safeBudget);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(callerCt);
            linkedCts.CancelAfter(_workflowOptions.Value.MetadataInternalTimeoutMs);

            try
            {
                var result = await _enrichmentOrchestrator.WaitForEnrichmentAsync(prNumber, linkedCts.Token);
                var byPage = result.Allocation.Pages
                    .Select(p => (Page: p.PageNumber, Count: p.Items.Count))
                    .ToArray();
                var paging = (result.Allocation.TotalPages, result.Allocation.TotalItems, safeBudget, (IReadOnlyList<(int Page, int Count)>)byPage);
                return new PagingFetchResult(paging, Deferred: false, Failure: null);
            }
            catch (OperationCanceledException) when (!callerCt.IsCancellationRequested)
            {
                _logger.LogInformation(
                    "PR {PrNumber} enrichment did not complete within {TimeoutMs}ms internal timeout; returning basic summary",
                    prNumber, _workflowOptions.Value.MetadataInternalTimeoutMs);
                return new PagingFetchResult(null, Deferred: true, Failure: null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Background body raised — turn it into a friendly failure block, not a tool-call error.
                var failure = PrEnrichmentFailure.From(ex);
                return new PagingFetchResult(null, Deferred: false, Failure: failure);
            }
        }

        private readonly record struct PagingFetchResult(
            (int TotalPages, int TotalFiles, int BudgetPerPage, IReadOnlyList<(int Page, int Count)> ByPage)? Paging,
            bool Deferred,
            PrEnrichmentFailure? Failure);
    }
}
