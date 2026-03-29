using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Models.Pagination;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Properties;
using REBUSS.Pure.Services.ResponsePacking;
using REBUSS.Pure.Tools.Models;

namespace REBUSS.Pure.Tools
{
    /// <summary>
    /// Handles the <c>get_pr_content</c> MCP tool.
    /// Returns diff content for a single page of a pull request review.
    /// Pages are computed via stat-based token estimation and deterministic page allocation.
    /// </summary>
    [McpServerToolType]
    public class GetPullRequestContentToolHandler
    {
        private readonly IPullRequestDataProvider _dataProvider;
        private readonly IContextBudgetResolver _budgetResolver;
        private readonly ITokenEstimator _tokenEstimator;
        private readonly IFileClassifier _fileClassifier;
        private readonly IPageAllocator _pageAllocator;
        private readonly ILogger<GetPullRequestContentToolHandler> _logger;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public GetPullRequestContentToolHandler(
            IPullRequestDataProvider dataProvider,
            IContextBudgetResolver budgetResolver,
            ITokenEstimator tokenEstimator,
            IFileClassifier fileClassifier,
            IPageAllocator pageAllocator,
            ILogger<GetPullRequestContentToolHandler> logger)
        {
            _dataProvider = dataProvider;
            _budgetResolver = budgetResolver;
            _tokenEstimator = tokenEstimator;
            _fileClassifier = fileClassifier;
            _pageAllocator = pageAllocator;
            _logger = logger;
        }

        [McpServerTool(Name = "get_pr_content"), Description(
            "Returns the diff content for a specific page of a pull request review. " +
            "Pages are determined by stat-based token estimation and the provided budget. " +
            "Use get_pr_metadata with modelName/maxTokens to discover the total page count first.")]
        public async Task<string> ExecuteAsync(
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
                _logger.LogInformation(Resources.LogGetPrContentEntry,
                    prNumber, pageNumber);
                var sw = Stopwatch.StartNew();

                var budget = _budgetResolver.Resolve(maxTokens, modelName);
                var safeBudget = budget.SafeBudgetTokens;

                // 1. Fetch lightweight file list and build page allocation
                var files = await _dataProvider.GetFilesAsync(prNumber.Value, cancellationToken);
                var candidates = BuildStatBasedCandidates(files.Files);
                candidates.Sort(PackingPriorityComparer.Instance);

                var allocation = _pageAllocator.Allocate(candidates, safeBudget);

                if (pageNumber > allocation.TotalPages)
                    throw new McpException(
                        string.Format(Resources.ErrorPageNumberExceedsTotalPages, pageNumber, allocation.TotalPages));

                var pageSlice = allocation.Pages[pageNumber.Value - 1];

                // 2. Collect file paths on this page
                var pageFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in pageSlice.Items)
                    pageFilePaths.Add(candidates[item.OriginalIndex].Path);

                // 3. Fetch full diff and filter to page files
                var diff = await _dataProvider.GetDiffAsync(prNumber.Value, cancellationToken);
                var pageFiles = BuildPageFileChanges(diff, pageFilePaths);

                // 4. Build category breakdown
                var categories = BuildCategoryBreakdown(pageFilePaths, candidates);

                var summary = new ContentPageSummary(
                    FilesOnPage: pageFiles.Count,
                    TotalFiles: allocation.TotalItems,
                    EstimatedTokens: pageSlice.BudgetUsed,
                    HasMorePages: pageNumber.Value < allocation.TotalPages,
                    Categories: categories);

                var result = new PullRequestContentPageResult(
                    PrNumber: prNumber.Value,
                    PageNumber: pageNumber.Value,
                    TotalPages: allocation.TotalPages,
                    Files: pageFiles,
                    Summary: summary);

                var json = JsonSerializer.Serialize(result, JsonOptions);
                sw.Stop();

                _logger.LogInformation(
                    Resources.LogGetPrContentCompleted,
                    prNumber, pageNumber, allocation.TotalPages, pageFiles.Count, json.Length, sw.ElapsedMilliseconds);

                return json;
            }
            catch (PullRequestNotFoundException ex)
            {
                _logger.LogWarning(ex, Resources.LogGetPrContentPrNotFound, prNumber);
                throw new McpException(string.Format(Resources.ErrorPullRequestNotFound, ex.Message));
            }
            catch (McpException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, Resources.LogGetPrContentError,
                    prNumber, pageNumber);
                throw new McpException(string.Format(Resources.ErrorRetrievingPrContent, ex.Message));
            }
        }

        // --- Helpers ---------------------------------------------------------------

        private List<PackingCandidate> BuildStatBasedCandidates(List<PullRequestFileInfo> files)
        {
            var candidates = new List<PackingCandidate>(files.Count);
            foreach (var file in files)
            {
                var estimatedTokens = _tokenEstimator.EstimateFromStats(file.Additions, file.Deletions);
                var classification = _fileClassifier.Classify(file.Path);
                candidates.Add(new PackingCandidate(
                    file.Path,
                    estimatedTokens,
                    classification.Category,
                    file.Additions + file.Deletions));
            }
            return candidates;
        }

        private static List<StructuredFileChange> BuildPageFileChanges(
            PullRequestDiff diff, HashSet<string> pageFilePaths)
        {
            return diff.Files
                .Where(f => pageFilePaths.Contains(f.Path))
                .Select(f => new StructuredFileChange
                {
                    Path = f.Path,
                    ChangeType = f.ChangeType,
                    SkipReason = f.SkipReason,
                    Additions = f.Additions,
                    Deletions = f.Deletions,
                    Hunks = f.Hunks.Select(h => new StructuredHunk
                    {
                        OldStart = h.OldStart,
                        OldCount = h.OldCount,
                        NewStart = h.NewStart,
                        NewCount = h.NewCount,
                        Lines = h.Lines.Select(l => new StructuredLine
                        {
                            Op = l.Op.ToString(),
                            Text = l.Text
                        }).ToList()
                    }).ToList()
                }).ToList();
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
