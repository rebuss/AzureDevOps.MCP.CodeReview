using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Properties;
using REBUSS.Pure.Services.ResponsePacking;
using REBUSS.Pure.Tools.Shared;

namespace REBUSS.Pure.Tools
{
    [McpServerToolType]
    public class GetPullRequestContentToolHandler
    {
        private readonly IPullRequestDiffCache _diffCache;
        private readonly IContextBudgetResolver _budgetResolver;
        private readonly ITokenEstimator _tokenEstimator;
        private readonly IFileClassifier _fileClassifier;
        private readonly IPageAllocator _pageAllocator;
        private readonly ILogger<GetPullRequestContentToolHandler> _logger;

        public GetPullRequestContentToolHandler(
            IPullRequestDiffCache diffCache,
            IContextBudgetResolver budgetResolver,
            ITokenEstimator tokenEstimator,
            IFileClassifier fileClassifier,
            IPageAllocator pageAllocator,
            ILogger<GetPullRequestContentToolHandler> logger)
        {
            _diffCache = diffCache;
            _budgetResolver = budgetResolver;
            _tokenEstimator = tokenEstimator;
            _fileClassifier = fileClassifier;
            _pageAllocator = pageAllocator;
            _logger = logger;
        }

        [McpServerTool(Name = "get_pr_content"), Description(
            "Returns plain-text diff content for a specific page of a pull request review. " +
            "One content block per file with -/+/space prefixed lines, plus a pagination footer. " +
            "Use get_pr_metadata with modelName/maxTokens to discover the total page count first.")]
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

                var diff = await _diffCache.GetOrFetchDiffAsync(prNumber.Value, ct: cancellationToken);
                var candidates = FileTokenMeasurement.BuildCandidatesFromDiff(diff, _tokenEstimator, _fileClassifier);
                candidates.Sort(PackingPriorityComparer.Instance);

                var allocation = _pageAllocator.Allocate(candidates, safeBudget);

                if (pageNumber > allocation.TotalPages)
                    throw new McpException(
                        string.Format(Resources.ErrorPageNumberExceedsTotalPages, pageNumber, allocation.TotalPages));

                var pageSlice = allocation.Pages[pageNumber.Value - 1];

                var pageFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in pageSlice.Items)
                    pageFilePaths.Add(candidates[item.OriginalIndex].Path);

                var categories = BuildCategoryBreakdown(pageFilePaths, candidates);

                var pageFiles = diff.Files
                    .Where(f => pageFilePaths.Contains(f.Path))
                    .Select(f => FileTokenMeasurement.MapToStructured(f))
                    .ToList();

                var blocks = new List<ContentBlock>(pageFiles.Count + 1);
                foreach (var f in pageFiles)
                    blocks.Add(new TextContentBlock { Text = PlainTextFormatter.FormatFileDiff(f) });

                blocks.Add(new TextContentBlock
                {
                    Text = PlainTextFormatter.FormatSimplePaginationBlock(
                        pageNumber.Value, allocation.TotalPages,
                        pageFiles.Count, allocation.TotalItems,
                        pageSlice.BudgetUsed,
                        categories)
                });

                sw.Stop();
                _logger.LogInformation(Resources.LogGetPrContentCompleted,
                    prNumber, pageNumber, allocation.TotalPages, pageFiles.Count, sw.ElapsedMilliseconds);

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

        private static Dictionary<string, int> BuildCategoryBreakdown(
            HashSet<string> pageFilePaths, List<PackingCandidate> candidates)
        {
            var categories = new Dictionary<string, int>();
            foreach (var candidate in candidates)
            {
                if (!pageFilePaths.Contains(candidate.Path)) continue;
                var key = candidate.Category.ToString().ToLowerInvariant();
                categories[key] = categories.GetValueOrDefault(key) + 1;
            }
            return categories;
        }
    }
}