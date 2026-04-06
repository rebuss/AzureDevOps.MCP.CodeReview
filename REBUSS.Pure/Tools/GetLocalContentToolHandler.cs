using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Properties;
using REBUSS.Pure.Services.LocalReview;
using REBUSS.Pure.Services.Pagination;
using REBUSS.Pure.Services.ResponsePacking;
using REBUSS.Pure.Tools.Models;
using REBUSS.Pure.Tools.Shared;

namespace REBUSS.Pure.Tools
{
    [McpServerToolType]
    public class GetLocalContentToolHandler
    {
        private readonly ILocalReviewProvider _localProvider;
        private readonly IContextBudgetResolver _budgetResolver;
        private readonly ITokenEstimator _tokenEstimator;
        private readonly IFileClassifier _fileClassifier;
        private readonly IPageAllocator _pageAllocator;
        private readonly ICodeProcessor _codeProcessor;
        private readonly ILogger<GetLocalContentToolHandler> _logger;

        public GetLocalContentToolHandler(
            ILocalReviewProvider localProvider,
            IContextBudgetResolver budgetResolver,
            ITokenEstimator tokenEstimator,
            IFileClassifier fileClassifier,
            IPageAllocator pageAllocator,
            ICodeProcessor codeProcessor,
            ILogger<GetLocalContentToolHandler> logger)
        {
            _localProvider = localProvider;
            _budgetResolver = budgetResolver;
            _tokenEstimator = tokenEstimator;
            _fileClassifier = fileClassifier;
            _pageAllocator = pageAllocator;
            _codeProcessor = codeProcessor;
            _logger = logger;
        }

        [McpServerTool(Name = "get_local_content"), Description(
            "Returns plain-text diff content for a specific page of local uncommitted changes. " +
            "One content block per file with -/+/space prefixed lines, plus a pagination footer. " +
            "The tool computes page allocation internally.")]
        public async Task<IEnumerable<ContentBlock>> ExecuteAsync(
            [Description("Page number to retrieve (1-based)")] int? pageNumber = null,
            [Description("Review scope: 'working-tree' (default), 'staged', or a branch/ref name")] string? scope = null,
            [Description("Model name for context budget resolution")] string? modelName = null,
            [Description("Explicit token budget override")] int? maxTokens = null,
            CancellationToken cancellationToken = default)
        {
            if (pageNumber == null)
                throw new McpException(Resources.ErrorMissingRequiredPageNumber);
            if (pageNumber < 1)
                throw new McpException(Resources.ErrorPageNumberMustBePositive);

            try
            {
                var parsedScope = LocalReviewScope.Parse(scope);
                _logger.LogInformation(Resources.LogGetLocalContentEntry, pageNumber, parsedScope);
                var sw = Stopwatch.StartNew();

                var budget = _budgetResolver.Resolve(maxTokens, modelName);
                var safeBudget = budget.SafeBudgetTokens;

                var localFiles = await _localProvider.GetFilesAsync(parsedScope, cancellationToken);

                // Fetch every file's diff upfront so we can enrich and measure against
                // the post-enrichment text. Pagination must reflect the size that will
                // actually be emitted; stat-based estimation under-counts dramatically
                // once enrichers inject scope/structural/call-site annotations and
                // before/after windows, causing pages to blow past the budget.
                var diffByPath = new Dictionary<string, FileChange>(StringComparer.OrdinalIgnoreCase);
                var fetchTasks = localFiles.Files
                    .Select(f => _localProvider.GetFileDiffAsync(f.Path, parsedScope, cancellationToken))
                    .ToList();
                await Task.WhenAll(fetchTasks);
                foreach (var task in fetchTasks)
                {
                    var fc = task.Result.Files.FirstOrDefault();
                    if (fc != null)
                        diffByPath[fc.Path] = fc;
                }

                var aggregatedDiff = new PullRequestDiff
                {
                    Files = diffByPath.Values.ToList()
                };

                var (candidates, enrichedByPath) = await FileTokenMeasurement.BuildEnrichedCandidatesAsync(
                    aggregatedDiff, _tokenEstimator, _fileClassifier, _codeProcessor, cancellationToken);
                candidates.Sort(PackingPriorityComparer.Instance);

                var allocation = _pageAllocator.Allocate(candidates, safeBudget);

                if (pageNumber > allocation.TotalPages)
                    throw new McpException(
                        string.Format(Resources.ErrorPageNumberExceedsTotalPages, pageNumber, allocation.TotalPages));

                var pageSlice = allocation.Pages[pageNumber.Value - 1];

                var pageCandidateIndices = pageSlice.Items.Select(i => i.OriginalIndex).ToList();
                var pagePaths = pageCandidateIndices.Select(i => candidates[i].Path).ToList();

                var categories = BuildCategoryBreakdown(pageCandidateIndices, candidates);

                var blocks = new List<ContentBlock>(pagePaths.Count + 2);
                blocks.Add(new TextContentBlock
                {
                    Text = PlainTextFormatter.FormatLocalContentHeader(
                        localFiles.RepositoryRoot,
                        localFiles.CurrentBranch,
                        parsedScope.ToString())
                });
                foreach (var path in pagePaths)
                    blocks.Add(new TextContentBlock { Text = enrichedByPath[path] });

                blocks.Add(new TextContentBlock
                {
                    Text = PlainTextFormatter.FormatSimplePaginationBlock(
                        pageNumber.Value, allocation.TotalPages,
                        pagePaths.Count, allocation.TotalItems,
                        pageSlice.BudgetUsed,
                        categories)
                });

                sw.Stop();
                _logger.LogInformation(Resources.LogGetLocalContentCompleted,
                    pageNumber, allocation.TotalPages, pagePaths.Count, sw.ElapsedMilliseconds);

                return blocks;
            }
            catch (McpException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, Resources.LogGetLocalContentError, pageNumber, scope);
                throw new McpException(string.Format(Resources.ErrorRetrievingLocalContent, ex.Message));
            }
        }

        private static Dictionary<string, int> BuildCategoryBreakdown(
            List<int> pageCandidateIndices, List<PackingCandidate> candidates)
        {
            var categories = new Dictionary<string, int>();
            foreach (var idx in pageCandidateIndices)
            {
                var key = candidates[idx].Category.ToString().ToLowerInvariant();
                categories[key] = categories.GetValueOrDefault(key) + 1;
            }
            return categories;
        }
    }
}