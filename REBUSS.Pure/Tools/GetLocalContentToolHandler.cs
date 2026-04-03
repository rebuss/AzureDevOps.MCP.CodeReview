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
        private readonly ILogger<GetLocalContentToolHandler> _logger;

        public GetLocalContentToolHandler(
            ILocalReviewProvider localProvider,
            IContextBudgetResolver budgetResolver,
            ITokenEstimator tokenEstimator,
            IFileClassifier fileClassifier,
            IPageAllocator pageAllocator,
            ILogger<GetLocalContentToolHandler> logger)
        {
            _localProvider = localProvider;
            _budgetResolver = budgetResolver;
            _tokenEstimator = tokenEstimator;
            _fileClassifier = fileClassifier;
            _pageAllocator = pageAllocator;
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
                var candidates = BuildStatBasedCandidates(localFiles.Files);
                candidates.Sort(PackingPriorityComparer.Instance);

                var allocation = _pageAllocator.Allocate(candidates, safeBudget);

                if (pageNumber > allocation.TotalPages)
                    throw new McpException(
                        string.Format(Resources.ErrorPageNumberExceedsTotalPages, pageNumber, allocation.TotalPages));

                var pageSlice = allocation.Pages[pageNumber.Value - 1];

                var diffTasks = pageSlice.Items
                    .Select(item => (
                        Index: item.OriginalIndex,
                        Task: _localProvider.GetFileDiffAsync(
                            candidates[item.OriginalIndex].Path, parsedScope, cancellationToken)))
                    .ToList();

                await Task.WhenAll(diffTasks.Select(d => d.Task));

                var pageFiles = new List<StructuredFileChange>(diffTasks.Count);
                var pageCandidateIndices = new List<int>(diffTasks.Count);
                foreach (var (index, task) in diffTasks)
                {
                    pageCandidateIndices.Add(index);
                    var fileChange = task.Result.Files.FirstOrDefault();
                    if (fileChange != null)
                        pageFiles.Add(FileTokenMeasurement.MapToStructured(fileChange));
                }

                var categories = BuildCategoryBreakdown(pageCandidateIndices, candidates);

                var blocks = new List<ContentBlock>(pageFiles.Count + 2);
                blocks.Add(new TextContentBlock
                {
                    Text = PlainTextFormatter.FormatLocalContentHeader(
                        localFiles.RepositoryRoot,
                        localFiles.CurrentBranch,
                        parsedScope.ToString())
                });
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
                _logger.LogInformation(Resources.LogGetLocalContentCompleted,
                    pageNumber, allocation.TotalPages, pageFiles.Count, sw.ElapsedMilliseconds);

                return blocks;
            }
            catch (McpException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, Resources.LogGetLocalContentError, pageNumber, scope);
                throw new McpException(string.Format(Resources.ErrorRetrievingLocalContent, ex.Message));
            }
        }

        private List<PackingCandidate> BuildStatBasedCandidates(List<PullRequestFileInfo> files)
        {
            var candidates = new List<PackingCandidate>(files.Count);
            foreach (var file in files)
            {
                var estimatedTokens = file.Changes > 0
                    ? _tokenEstimator.EstimateFromStats(file.Additions, file.Deletions)
                    : PaginationConstants.FallbackEstimateWhenLinecountsUnknown;
                var classification = _fileClassifier.Classify(file.Path);
                candidates.Add(new PackingCandidate(
                    file.Path,
                    estimatedTokens,
                    classification.Category,
                    file.Additions + file.Deletions));
            }
            return candidates;
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