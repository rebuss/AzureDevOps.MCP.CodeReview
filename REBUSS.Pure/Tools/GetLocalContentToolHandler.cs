using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Services.LocalReview;
using REBUSS.Pure.Services.ResponsePacking;
using REBUSS.Pure.Tools.Models;

namespace REBUSS.Pure.Tools
{
    /// <summary>
    /// Handles the <c>get_local_content</c> MCP tool.
    /// Returns diff content for a single page of local uncommitted changes.
    /// Pages are computed via stat-based token estimation and deterministic page allocation.
    /// </summary>
    [McpServerToolType]
    public class GetLocalContentToolHandler
    {
        private readonly ILocalReviewProvider _localProvider;
        private readonly IContextBudgetResolver _budgetResolver;
        private readonly ITokenEstimator _tokenEstimator;
        private readonly IFileClassifier _fileClassifier;
        private readonly IPageAllocator _pageAllocator;
        private readonly ILogger<GetLocalContentToolHandler> _logger;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

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
            "Returns the diff content for a specific page of local uncommitted changes. " +
            "Pages are determined by stat-based token estimation and the provided budget. " +
            "The tool computes page allocation internally — no separate metadata call is needed.")]
        public async Task<string> ExecuteAsync(
            [Description("Page number to retrieve (1-based)")] int? pageNumber = null,
            [Description("Review scope: 'working-tree' (default), 'staged', or a branch/ref name")] string? scope = null,
            [Description("Model name for context budget resolution")] string? modelName = null,
            [Description("Explicit token budget override")] int? maxTokens = null,
            CancellationToken cancellationToken = default)
        {
            if (pageNumber == null)
                throw new McpException("Missing required parameter: pageNumber");
            if (pageNumber < 1)
                throw new McpException("pageNumber must be >= 1");

            try
            {
                var parsedScope = LocalReviewScope.Parse(scope);
                _logger.LogInformation("[get_local_content] Entry: page {PageNumber}, scope={Scope}",
                    pageNumber, parsedScope);

                var sw = Stopwatch.StartNew();

                var budget = _budgetResolver.Resolve(maxTokens, modelName);
                var safeBudget = budget.SafeBudgetTokens;

                // 1. Fetch lightweight file list and build page allocation
                var localFiles = await _localProvider.GetFilesAsync(parsedScope, cancellationToken);
                var candidates = BuildStatBasedCandidates(localFiles.Files);
                candidates.Sort(PackingPriorityComparer.Instance);

                var allocation = _pageAllocator.Allocate(candidates, safeBudget);

                if (pageNumber > allocation.TotalPages)
                    throw new McpException(
                        $"pageNumber {pageNumber} exceeds total pages {allocation.TotalPages}");

                var pageSlice = allocation.Pages[pageNumber.Value - 1];

                // 2. Fetch diffs only for files on this page (per-file — inherently efficient)
                var pageFiles = new List<StructuredFileChange>();
                var pageCandidateIndices = new List<int>();
                foreach (var item in pageSlice.Items)
                {
                    var candidate = candidates[item.OriginalIndex];
                    pageCandidateIndices.Add(item.OriginalIndex);

                    var fileDiff = await _localProvider.GetFileDiffAsync(
                        candidate.Path, parsedScope, cancellationToken);

                    var fileChange = fileDiff.Files.FirstOrDefault();
                    if (fileChange != null)
                        pageFiles.Add(MapToStructuredFileChange(fileChange));
                }

                // 3. Build category breakdown
                var categories = BuildCategoryBreakdown(pageCandidateIndices, candidates);

                var summary = new ContentPageSummary(
                    FilesOnPage: pageFiles.Count,
                    TotalFiles: allocation.TotalItems,
                    EstimatedTokens: pageSlice.BudgetUsed,
                    HasMorePages: pageNumber.Value < allocation.TotalPages,
                    Categories: categories);

                var result = new LocalContentPageResult(
                    RepositoryRoot: localFiles.RepositoryRoot,
                    CurrentBranch: localFiles.CurrentBranch,
                    Scope: parsedScope.ToString(),
                    PageNumber: pageNumber.Value,
                    TotalPages: allocation.TotalPages,
                    Files: pageFiles,
                    Summary: summary);

                var json = JsonSerializer.Serialize(result, JsonOptions);
                sw.Stop();

                _logger.LogInformation(
                    "[get_local_content] Completed: page {Page}/{TotalPages}, {FileCount} files, {Chars} chars, {Ms}ms",
                    pageNumber, allocation.TotalPages, pageFiles.Count, json.Length, sw.ElapsedMilliseconds);

                return json;
            }
            catch (McpException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[get_local_content] Error (pageNumber={PageNumber}, scope={Scope})",
                    pageNumber, scope);
                throw new McpException($"Error retrieving local content: {ex.Message}");
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

        private static StructuredFileChange MapToStructuredFileChange(FileChange f)
        {
            return new StructuredFileChange
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
            };
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
