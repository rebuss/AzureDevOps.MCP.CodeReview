using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models.Pagination;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Properties;
using REBUSS.Pure.Services.LocalReview;
using REBUSS.Pure.Services.Pagination;
using REBUSS.Pure.Services.ResponsePacking;
using REBUSS.Pure.Tools.Models;

namespace REBUSS.Pure.Tools
{
    /// <summary>
    /// Handles the <c>get_local_files</c> MCP tool.
    /// Returns a classified list of locally changed files so an AI agent can
    /// decide which files to inspect in detail.
    /// Integrates with response packing (F003) and deterministic pagination (F004).
    /// </summary>
    [McpServerToolType]
    public class GetLocalChangesFilesToolHandler
    {
        private readonly ILocalReviewProvider _reviewProvider;
        private readonly IResponsePacker _packer;
        private readonly IContextBudgetResolver _budgetResolver;
        private readonly ITokenEstimator _tokenEstimator;
        private readonly IFileClassifier _fileClassifier;
        private readonly IPageAllocator _pageAllocator;
        private readonly IPageReferenceCodec _pageReferenceCodec;
        private readonly ILogger<GetLocalChangesFilesToolHandler> _logger;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public GetLocalChangesFilesToolHandler(
            ILocalReviewProvider reviewProvider,
            IResponsePacker packer,
            IContextBudgetResolver budgetResolver,
            ITokenEstimator tokenEstimator,
            IFileClassifier fileClassifier,
            IPageAllocator pageAllocator,
            IPageReferenceCodec pageReferenceCodec,
            ILogger<GetLocalChangesFilesToolHandler> logger)
        {
            _reviewProvider = reviewProvider;
            _packer = packer;
            _budgetResolver = budgetResolver;
            _tokenEstimator = tokenEstimator;
            _fileClassifier = fileClassifier;
            _pageAllocator = pageAllocator;
            _pageReferenceCodec = pageReferenceCodec;
            _logger = logger;
        }

        [McpServerTool(Name = "get_local_files"), Description(
            "Lists all locally changed files in the git repository with classification metadata " +
            "(status, extension, binary/generated/test flags, review priority) and a summary by category. " +
            "Use this as the first step of a self-review to discover what changed before inspecting diffs. " +
            "Supported scopes: 'working-tree' (default, staged + unstaged vs HEAD), " +
            "'staged' (index vs HEAD only), or any branch/ref name to diff the current branch against it.")]
        public async Task<string> ExecuteAsync(
            [Description("The change scope to review. 'working-tree' (default): all uncommitted changes vs HEAD. 'staged': only staged (indexed) changes vs HEAD. Any other value is treated as a base branch/ref.")] string? scope = null,
            [Description("Optional model name (e.g. 'Claude Sonnet') to resolve context window size")] string? modelName = null,
            [Description("Optional explicit context window size in tokens")] int? maxTokens = null,
            [Description("Opaque page reference from a previous response. Encodes all context needed to re-derive the page.")] string? pageReference = null,
            [Description("Page number for direct access (requires original params + budget)")] int? pageNumber = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var mutualExclError = PaginationOrchestrator.ValidateInputs(pageReference, pageNumber);
                if (mutualExclError != null)
                    throw new McpException(mutualExclError);

                var hasExplicitBudget = modelName != null || maxTokens != null;

                var budget = _budgetResolver.Resolve(maxTokens, modelName);

                var resolution = PaginationOrchestrator.ResolvePage(
                    pageReference, pageNumber, _pageReferenceCodec, budget.SafeBudgetTokens, hasExplicitBudget);

                if (!resolution.IsSuccess)
                    throw new McpException(resolution.ErrorMessage!);

                // Extract scope from page reference if provided
                var effectiveScope = scope;
                if (resolution.DecodedParams != null)
                {
                    if (resolution.DecodedParams.Value.TryGetProperty("scope", out var decodedScope))
                        effectiveScope = decodedScope.GetString();

                    if (scope != null && effectiveScope != null)
                    {
                        var scopeJson = JsonDocument.Parse($"\"{scope}\"").RootElement;
                        var paramError = PaginationOrchestrator.ValidateParameterMatch(
                            resolution.DecodedParams, "scope", scopeJson);
                        if (paramError != null)
                            throw new McpException(paramError);
                    }
                }

                var parsedScope = LocalReviewScope.Parse(effectiveScope);
                var effectiveBudget = resolution.ResolvedBudget;

                _logger.LogInformation(Resources.LogGetLocalFilesEntry, parsedScope);
                var sw = Stopwatch.StartNew();

                var reviewFiles = await _reviewProvider.GetFilesAsync(parsedScope, cancellationToken);

                if (!hasExplicitBudget && pageReference == null)
                {
                    // Feature 003 path
                    var fileItems003 = BuildFileItems(reviewFiles);
                    var candidates003 = BuildCandidates(fileItems003, budget.SafeBudgetTokens);
                    var decision = _packer.Pack(candidates003, budget.SafeBudgetTokens);

                    var packedFiles003 = new List<PullRequestFileItem>();
                    for (var i = 0; i < decision.Items.Count; i++)
                    {
                        if (decision.Items[i].Status != PackingItemStatus.Deferred)
                            packedFiles003.Add(fileItems003[i]);
                    }

                    var result003 = new LocalReviewFilesResult
                    {
                        RepositoryRoot = reviewFiles.RepositoryRoot,
                        Scope = reviewFiles.Scope,
                        CurrentBranch = reviewFiles.CurrentBranch,
                        TotalFiles = reviewFiles.Files.Count,
                        Files = packedFiles003,
                        Summary = BuildSummary(reviewFiles),
                        Manifest = MapManifest(decision.Manifest)
                    };

                    var json003 = JsonSerializer.Serialize(result003, JsonOptions);
                    sw.Stop();
                    _logger.LogInformation(Resources.LogGetLocalFilesCompletedF003,
                        parsedScope, reviewFiles.Files.Count, sw.ElapsedMilliseconds);
                    return json003;
                }

                // Feature 004 path
                var fileItems = BuildFileItems(reviewFiles);
                var candidates = BuildCandidates(fileItems, effectiveBudget);
                var sortedCandidates = SortCandidates(candidates);

                PageAllocation allocation;
                try
                {
                    allocation = _pageAllocator.Allocate(sortedCandidates, effectiveBudget);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("too small"))
                {
                    throw new McpException(ex.Message);
                }

                var requestedPage = resolution.PageNumber;
                if (requestedPage < 1 || requestedPage > allocation.TotalPages)
                    throw new McpException(string.Format(Resources.ErrorPageNumberOutOfRange, requestedPage, allocation.TotalPages));

                var pageSlice = allocation.Pages[requestedPage - 1];
                var packedFiles = ExtractPageFiles(fileItems, sortedCandidates, pageSlice);

                var scopeForRef = effectiveScope ?? "working-tree";
                var requestParams = JsonDocument.Parse($"{{\"scope\":\"{scopeForRef}\"}}").RootElement;

                var paginationMeta = PaginationOrchestrator.BuildPaginationMetadata(
                    allocation, requestedPage, _pageReferenceCodec,
                    "get_local_files", requestParams, effectiveBudget, null);

                var manifestResult = BuildPageManifest(sortedCandidates, pageSlice, allocation, effectiveBudget);

                var result = new LocalReviewFilesResult
                {
                    RepositoryRoot = reviewFiles.RepositoryRoot,
                    Scope = reviewFiles.Scope,
                    CurrentBranch = reviewFiles.CurrentBranch,
                    TotalFiles = reviewFiles.Files.Count,
                    Files = packedFiles,
                    Summary = BuildSummary(reviewFiles),
                    Manifest = manifestResult,
                    Pagination = paginationMeta
                };

                var json = JsonSerializer.Serialize(result, JsonOptions);
                sw.Stop();
                _logger.LogInformation(
                    Resources.LogGetLocalFilesCompletedF004,
                    parsedScope, requestedPage, allocation.TotalPages, sw.ElapsedMilliseconds);

                return json;
            }
            catch (LocalRepositoryNotFoundException ex)
            {
                _logger.LogWarning(ex, Resources.LogGetLocalFilesRepositoryNotFound);
                throw new McpException(string.Format(Resources.ErrorRepositoryNotFound, ex.Message));
            }
            catch (GitCommandException ex)
            {
                _logger.LogWarning(ex, Resources.LogGetLocalFilesGitCommandFailed);
                throw new McpException(string.Format(Resources.ErrorGitCommandFailed, ex.Message));
            }
            catch (McpException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, Resources.LogGetLocalFilesError);
                throw new McpException(string.Format(Resources.ErrorRetrievingLocalFiles, ex.Message));
            }
        }

        // --- Build helpers ---

        private static List<PullRequestFileItem> BuildFileItems(LocalReviewFiles reviewFiles)
        {
            return reviewFiles.Files.Select(f => new PullRequestFileItem
            {
                Path = f.Path,
                Status = f.Status,
                Additions = f.Additions,
                Deletions = f.Deletions,
                Changes = f.Changes,
                Extension = f.Extension,
                IsBinary = f.IsBinary,
                IsGenerated = f.IsGenerated,
                IsTestFile = f.IsTestFile,
                ReviewPriority = f.ReviewPriority
            }).ToList();
        }

        private List<PackingCandidate> BuildCandidates(List<PullRequestFileItem> fileItems, int safeBudgetTokens)
        {
            var candidates = new List<PackingCandidate>(fileItems.Count);
            for (var i = 0; i < fileItems.Count; i++)
            {
                var fi = fileItems[i];
                var serialized = JsonSerializer.Serialize(fi, JsonOptions);
                var estimation = _tokenEstimator.Estimate(serialized, safeBudgetTokens);
                var classification = _fileClassifier.Classify(fi.Path);

                candidates.Add(new PackingCandidate(
                    fi.Path,
                    estimation.EstimatedTokens,
                    classification.Category,
                    fi.Additions + fi.Deletions));
            }
            return candidates;
        }

        private static List<PackingCandidate> SortCandidates(List<PackingCandidate> candidates)
        {
            var sorted = new List<PackingCandidate>(candidates);
            sorted.Sort(PackingPriorityComparer.Instance);
            return sorted;
        }

        private static List<PullRequestFileItem> ExtractPageFiles(
            List<PullRequestFileItem> allFiles,
            List<PackingCandidate> candidates,
            PageSlice pageSlice)
        {
            var pageFiles = new List<PullRequestFileItem>();
            foreach (var item in pageSlice.Items)
            {
                var candidate = candidates[item.OriginalIndex];
                var fileItem = allFiles.FirstOrDefault(f => f.Path == candidate.Path);
                if (fileItem != null)
                    pageFiles.Add(fileItem);
            }
            return pageFiles;
        }

        private ContentManifestResult BuildPageManifest(
            List<PackingCandidate> candidates,
            PageSlice pageSlice,
            PageAllocation allocation,
            int safeBudgetTokens)
        {
            var entries = pageSlice.Items.Select(item =>
            {
                var candidate = candidates[item.OriginalIndex];
                return new ManifestEntryResult
                {
                    Path = candidate.Path,
                    EstimatedTokens = item.EstimatedTokens,
                    Status = item.Status.ToString(),
                    PriorityTier = candidate.Category.ToString()
                };
            }).ToList();

            return new ContentManifestResult
            {
                Items = entries,
                Summary = PaginationOrchestrator.BuildExtendedManifestSummary(pageSlice, allocation, safeBudgetTokens)
            };
        }

        private static PullRequestFilesSummaryResult BuildSummary(LocalReviewFiles reviewFiles)
        {
            return new PullRequestFilesSummaryResult
            {
                SourceFiles = reviewFiles.Summary.SourceFiles,
                TestFiles = reviewFiles.Summary.TestFiles,
                ConfigFiles = reviewFiles.Summary.ConfigFiles,
                DocsFiles = reviewFiles.Summary.DocsFiles,
                BinaryFiles = reviewFiles.Summary.BinaryFiles,
                GeneratedFiles = reviewFiles.Summary.GeneratedFiles,
                HighPriorityFiles = reviewFiles.Summary.HighPriorityFiles
            };
        }

        private static ContentManifestResult MapManifest(ContentManifest manifest)
        {
            return new ContentManifestResult
            {
                Items = manifest.Items.Select(e => new ManifestEntryResult
                {
                    Path = e.Path,
                    EstimatedTokens = e.EstimatedTokens,
                    Status = e.Status.ToString(),
                    PriorityTier = e.PriorityTier
                }).ToList(),
                Summary = new ManifestSummaryResult
                {
                    TotalItems = manifest.Summary.TotalItems,
                    IncludedCount = manifest.Summary.IncludedCount,
                    PartialCount = manifest.Summary.PartialCount,
                    DeferredCount = manifest.Summary.DeferredCount,
                    TotalBudgetTokens = manifest.Summary.TotalBudgetTokens,
                    BudgetUsed = manifest.Summary.BudgetUsed,
                    BudgetRemaining = manifest.Summary.BudgetRemaining,
                    UtilizationPercent = manifest.Summary.UtilizationPercent
                }
            };
        }
    }
}
