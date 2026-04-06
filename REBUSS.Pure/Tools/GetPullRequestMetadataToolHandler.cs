using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Properties;
using REBUSS.Pure.Services.ResponsePacking;
using REBUSS.Pure.Tools.Shared;

namespace REBUSS.Pure.Tools
{
    /// <summary>
    /// Handles the execution of the get_pr_metadata MCP tool.
    /// Validates input, delegates to <see cref="IPullRequestDataProvider"/>,
    /// and formats the result as a plain-text response.
    /// When budget parameters are provided, also computes pagination info.
    /// </summary>
    [McpServerToolType]
    public class GetPullRequestMetadataToolHandler
    {
        private readonly IPullRequestDataProvider _metadataProvider;
        private readonly IContextBudgetResolver _budgetResolver;
        private readonly ITokenEstimator _tokenEstimator;
        private readonly IFileClassifier _fileClassifier;
        private readonly IPageAllocator _pageAllocator;
        private readonly IPullRequestDiffCache _diffCache;
        private readonly IRepositoryDownloadOrchestrator _downloadOrchestrator;
        private readonly ICodeProcessor _codeProcessor;
        private readonly ILogger<GetPullRequestMetadataToolHandler> _logger;

        public GetPullRequestMetadataToolHandler(
            IPullRequestDataProvider metadataProvider,
            IContextBudgetResolver budgetResolver,
            ITokenEstimator tokenEstimator,
            IFileClassifier fileClassifier,
            IPageAllocator pageAllocator,
            IPullRequestDiffCache diffCache,
            IRepositoryDownloadOrchestrator downloadOrchestrator,
            ICodeProcessor codeProcessor,
            ILogger<GetPullRequestMetadataToolHandler> logger)
        {
            _metadataProvider = metadataProvider;
            _budgetResolver = budgetResolver;
            _tokenEstimator = tokenEstimator;
            _fileClassifier = fileClassifier;
            _pageAllocator = pageAllocator;
            _diffCache = diffCache;
            _downloadOrchestrator = downloadOrchestrator;
            _codeProcessor = codeProcessor;
            _logger = logger;
        }

        [McpServerTool(Name = "get_pr_metadata"), Description(
            "Retrieves metadata for a specific Pull Request. " +
            "Returns a plain-text block with PR details including title, author, state, " +
            "branches, stats, commit SHAs, and description. " +
            "When modelName or maxTokens is provided, also returns content paging info " +
            "with page count and per-page file breakdown for use with get_pr_content.")]
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
                if (modelName != null || maxTokens != null)
                    paging = await BuildContentPagingAsync(prNumber.Value, metadata.LastMergeSourceCommitId, modelName, maxTokens, cancellationToken);

                var text = PlainTextFormatter.FormatMetadata(metadata, prNumber.Value, paging);
                sw.Stop();

                _logger.LogInformation(Resources.LogGetPrMetadataCompleted, prNumber, text.Length, sw.ElapsedMilliseconds);

                return [new TextContentBlock { Text = text }];
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

        private async Task<(int TotalPages, int TotalFiles, int BudgetPerPage, IReadOnlyList<(int Page, int Count)> ByPage)>
            BuildContentPagingAsync(int prNumber, string? knownHeadCommitId, string? modelName, int? maxTokens, CancellationToken ct)
        {
            var diff = await _diffCache.GetOrFetchDiffAsync(prNumber, knownHeadCommitId, ct);
            var budget = _budgetResolver.Resolve(maxTokens, modelName);
            var safeBudget = budget.SafeBudgetTokens;

            // Measure against the post-enrichment text so totalPages reported in metadata
            // matches what get_pr_content will actually emit.
            var (candidates, _) = await FileTokenMeasurement.BuildEnrichedCandidatesAsync(
                diff, _tokenEstimator, _fileClassifier, _codeProcessor, ct);
            candidates.Sort(PackingPriorityComparer.Instance);

            var allocation = _pageAllocator.Allocate(candidates, safeBudget);

            var byPage = allocation.Pages
                .Select(p => (Page: p.PageNumber, Count: p.Items.Count))
                .ToArray();

            return (allocation.TotalPages, allocation.TotalItems, safeBudget, byPage);
        }
    }
}

