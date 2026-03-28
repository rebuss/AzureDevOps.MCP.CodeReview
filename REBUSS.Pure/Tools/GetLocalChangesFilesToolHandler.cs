using System.Diagnostics;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models.Pagination;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Mcp;
using REBUSS.Pure.Mcp.Models;
using REBUSS.Pure.Services.LocalReview;
using REBUSS.Pure.Services.Pagination;
using REBUSS.Pure.Services.ResponsePacking;
using REBUSS.Pure.Tools.Models;
using System.Text.Json;

namespace REBUSS.Pure.Tools
{
    /// <summary>
    /// Handles the <c>get_local_files</c> MCP tool.
    /// Returns a classified list of locally changed files so an AI agent can
    /// decide which files to inspect in detail.
    /// Integrates with response packing (F003) and deterministic pagination (F004).
    /// </summary>
    public class GetLocalChangesFilesToolHandler : IMcpToolHandler
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

        public string ToolName => "get_local_files";

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

        public McpTool GetToolDefinition() => new()
        {
            Name = ToolName,
            Description =
                "Lists all locally changed files in the git repository with classification metadata " +
                "(status, extension, binary/generated/test flags, review priority) and a summary by category. " +
                "Use this as the first step of a self-review to discover what changed before inspecting diffs. " +
                "Supported scopes: 'working-tree' (default, staged + unstaged vs HEAD), " +
                "'staged' (index vs HEAD only), or any branch/ref name to diff the current branch against it.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["scope"] = new ToolProperty
                    {
                        Type = "string",
                        Description =
                            "The change scope to review. " +
                            "'working-tree' (default): all uncommitted changes vs HEAD. " +
                            "'staged': only staged (indexed) changes vs HEAD. " +
                            "Any other value is treated as a base branch/ref (e.g. 'main', 'origin/main') " +
                            "and returns all commits on the current branch not yet merged into that base."
                    },
                    ["modelName"] = new ToolProperty
                    {
                        Type = "string",
                        Description = "Optional model name (e.g. 'Claude Sonnet') to resolve context window size"
                    },
                    ["maxTokens"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "Optional explicit context window size in tokens"
                    },
                    ["pageReference"] = new ToolProperty
                    {
                        Type = "string",
                        Description = "Opaque page reference from a previous response. Encodes all context needed to re-derive the page."
                    },
                    ["pageNumber"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "Page number for direct access (requires original params + budget)"
                    }
                },
                Required = new List<string>() // scope optional per Q22 when pageReference used
            }
        };

        public async Task<ToolResult> ExecuteAsync(
            Dictionary<string, object>? arguments,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var pageReference = ExtractOptionalString(arguments, "pageReference");
                var pageNumber = ExtractOptionalInt(arguments, "pageNumber");

                var mutualExclError = PaginationOrchestrator.ValidateInputs(pageReference, pageNumber);
                if (mutualExclError != null)
                    return CreateErrorResult(mutualExclError);

                var scopeStr = ExtractScope(arguments);
                var modelName = ExtractOptionalString(arguments, "modelName");
                var maxTokens = ExtractOptionalInt(arguments, "maxTokens");
                var hasExplicitBudget = modelName != null || maxTokens != null;

                var budget = _budgetResolver.Resolve(maxTokens, modelName);

                var resolution = PaginationOrchestrator.ResolvePage(
                    pageReference, pageNumber, _pageReferenceCodec, budget.SafeBudgetTokens, hasExplicitBudget);

                if (!resolution.IsSuccess)
                    return CreateErrorResult(resolution.ErrorMessage!);

                // Extract scope from page reference if provided
                var effectiveScope = scopeStr;
                if (resolution.DecodedParams != null)
                {
                    if (resolution.DecodedParams.Value.TryGetProperty("scope", out var decodedScope))
                        effectiveScope = decodedScope.GetString();

                    // Validate scope match if agent also provided scope (FR-016/Q19)
                    if (scopeStr != null && effectiveScope != null)
                    {
                        var scopeJson = JsonDocument.Parse($"\"{scopeStr}\"").RootElement;
                        var paramError = PaginationOrchestrator.ValidateParameterMatch(
                            resolution.DecodedParams, "scope", scopeJson);
                        if (paramError != null)
                            return CreateErrorResult(paramError);
                    }
                }

                // Validate: either scope or pageReference must be present (scope has default though)
                var scope = LocalReviewScope.Parse(effectiveScope);

                var effectiveBudget = resolution.ResolvedBudget;

                _logger.LogInformation("[{ToolName}] Entry: scope={Scope}", ToolName, scope);
                var sw = Stopwatch.StartNew();

                var reviewFiles = await _reviewProvider.GetFilesAsync(scope, cancellationToken);

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
                    _logger.LogInformation("[{ToolName}] Completed (F003): scope={Scope}, {FileCount} files, {ElapsedMs}ms",
                        ToolName, scope, reviewFiles.Files.Count, sw.ElapsedMilliseconds);
                    return CreateSuccessResult(json003);
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
                    return CreateErrorResult(ex.Message);
                }

                var requestedPage = resolution.PageNumber;
                if (requestedPage < 1 || requestedPage > allocation.TotalPages)
                    return CreateErrorResult($"Page number {requestedPage} is out of range. Valid range: 1 to {allocation.TotalPages}.");

                var pageSlice = allocation.Pages[requestedPage - 1];
                var packedFiles = ExtractPageFiles(fileItems, sortedCandidates, pageSlice);

                // Build request params for page reference (scope)
                var scopeForRef = effectiveScope ?? "working-tree";
                var requestParams = JsonDocument.Parse($"{{\"scope\":\"{scopeForRef}\"}}").RootElement;

                // No staleness for local tools — fingerprint is null
                var paginationMeta = PaginationOrchestrator.BuildPaginationMetadata(
                    allocation, requestedPage, _pageReferenceCodec,
                    ToolName, requestParams, effectiveBudget, null);

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
                    "[{ToolName}] Completed (F004): scope={Scope}, page {Page}/{TotalPages}, {ElapsedMs}ms",
                    ToolName, scope, requestedPage, allocation.TotalPages, sw.ElapsedMilliseconds);

                return CreateSuccessResult(json);
            }
            catch (LocalRepositoryNotFoundException ex)
            {
                _logger.LogWarning(ex, "[{ToolName}] Repository not found", ToolName);
                return CreateErrorResult($"Repository not found: {ex.Message}");
            }
            catch (GitCommandException ex)
            {
                _logger.LogWarning(ex, "[{ToolName}] Git command failed", ToolName);
                return CreateErrorResult($"Git command failed: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{ToolName}] Error", ToolName);
                return CreateErrorResult($"Error retrieving local files: {ex.Message}");
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

        // --- Input extraction ---

        private static string? ExtractScope(Dictionary<string, object>? arguments)
        {
            if (arguments == null || !arguments.TryGetValue("scope", out var scopeObj))
                return null;

            return scopeObj is JsonElement jsonElement
                ? jsonElement.GetString()
                : scopeObj?.ToString();
        }

        private static string? ExtractOptionalString(Dictionary<string, object>? arguments, string key)
        {
            if (arguments == null || !arguments.TryGetValue(key, out var value)) return null;
            return value is JsonElement jsonElement ? jsonElement.GetString() : value?.ToString();
        }

        private static int? ExtractOptionalInt(Dictionary<string, object>? arguments, string key)
        {
            if (arguments == null || !arguments.TryGetValue(key, out var value)) return null;
            try
            {
                return value is JsonElement jsonElement ? jsonElement.GetInt32() : Convert.ToInt32(value);
            }
            catch
            {
                return null;
            }
        }

        private static ToolResult CreateSuccessResult(string text) => new()
        {
            Content = new List<ContentItem> { new() { Type = "text", Text = text } },
            IsError = false
        };

        private static ToolResult CreateErrorResult(string errorMessage) => new()
        {
            Content = new List<ContentItem> { new() { Type = "text", Text = $"Error: {errorMessage}" } },
            IsError = true
        };
    }
}
