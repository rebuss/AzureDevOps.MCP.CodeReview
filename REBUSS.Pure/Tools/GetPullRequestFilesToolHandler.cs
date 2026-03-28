using System.Diagnostics;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Models.Pagination;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Mcp;
using REBUSS.Pure.Mcp.Models;
using REBUSS.Pure.Services.Pagination;
using REBUSS.Pure.Services.ResponsePacking;
using REBUSS.Pure.Tools.Models;
using System.Text.Json;

namespace REBUSS.Pure.Tools
{
    /// <summary>
    /// Handles the execution of the get_pr_files MCP tool.
    /// Validates input, delegates to <see cref="IPullRequestFilesProvider"/>,
    /// and formats the result as a structured JSON response.
    /// Integrates with response packing (F003) and deterministic pagination (F004).
    /// </summary>
    public class GetPullRequestFilesToolHandler : IMcpToolHandler
    {
        private readonly IPullRequestDataProvider _filesProvider;
        private readonly IResponsePacker _packer;
        private readonly IContextBudgetResolver _budgetResolver;
        private readonly ITokenEstimator _tokenEstimator;
        private readonly IFileClassifier _fileClassifier;
        private readonly IPageAllocator _pageAllocator;
        private readonly IPageReferenceCodec _pageReferenceCodec;
        private readonly ILogger<GetPullRequestFilesToolHandler> _logger;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public string ToolName => "get_pr_files";

        public GetPullRequestFilesToolHandler(
            IPullRequestDataProvider filesProvider,
            IResponsePacker packer,
            IContextBudgetResolver budgetResolver,
            ITokenEstimator tokenEstimator,
            IFileClassifier fileClassifier,
            IPageAllocator pageAllocator,
            IPageReferenceCodec pageReferenceCodec,
            ILogger<GetPullRequestFilesToolHandler> logger)
        {
            _filesProvider = filesProvider;
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
            Description = "Retrieves structured information about all files changed in a specific Pull Request. " +
                          "Returns per-file metadata (status, additions, deletions, extension, " +
                          "binary/generated/test flags, review priority) and an aggregated summary by category.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["prNumber"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "The Pull Request number/ID to retrieve the file list for"
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
                Required = new List<string>() // prNumber optional per Q17/Q22 when pageReference used
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

                int? prNumber = null;
                if (arguments != null && arguments.TryGetValue("prNumber", out var prNumberObj))
                {
                    try
                    {
                        prNumber = prNumberObj is JsonElement jsonEl ? jsonEl.GetInt32() : Convert.ToInt32(prNumberObj);
                        if (prNumber <= 0)
                            return CreateErrorResult("prNumber must be greater than 0");
                    }
                    catch
                    {
                        return CreateErrorResult("Invalid prNumber parameter: must be an integer");
                    }
                }

                if (prNumber == null && pageReference == null)
                    return CreateErrorResult("Missing required parameter: prNumber");

                var modelName = ExtractOptionalString(arguments, "modelName");
                var maxTokens = ExtractOptionalInt(arguments, "maxTokens");
                var hasExplicitBudget = modelName != null || maxTokens != null;

                _logger.LogInformation("[{ToolName}] Entry: PR #{PrNumber}", ToolName, prNumber);
                var sw = Stopwatch.StartNew();

                var budget = _budgetResolver.Resolve(maxTokens, modelName);

                var resolution = PaginationOrchestrator.ResolvePage(
                    pageReference, pageNumber, _pageReferenceCodec, budget.SafeBudgetTokens, hasExplicitBudget);

                if (!resolution.IsSuccess)
                    return CreateErrorResult(resolution.ErrorMessage!);

                var effectivePrNumber = prNumber;
                if (resolution.DecodedParams != null)
                {
                    if (resolution.DecodedParams.Value.TryGetProperty("prNumber", out var decodedPr))
                        effectivePrNumber = decodedPr.GetInt32();

                    if (prNumber != null)
                    {
                        var prJsonElement = JsonDocument.Parse($"{prNumber}").RootElement;
                        var paramError = PaginationOrchestrator.ValidateParameterMatch(
                            resolution.DecodedParams, "prNumber", prJsonElement);
                        if (paramError != null)
                            return CreateErrorResult(paramError);
                    }
                }

                if (effectivePrNumber == null || effectivePrNumber <= 0)
                    return CreateErrorResult("Missing required parameter: prNumber");

                var effectiveBudget = resolution.ResolvedBudget;

                Task<FullPullRequestMetadata>? metadataTask = null;
                var isPageRefMode = pageReference != null;
                if (isPageRefMode && resolution.Fingerprint != null)
                    metadataTask = _filesProvider.GetMetadataAsync(effectivePrNumber.Value, cancellationToken);

                var prFiles = await _filesProvider.GetFilesAsync(effectivePrNumber.Value, cancellationToken);

                if (!hasExplicitBudget && pageReference == null)
                {
                    var result = BuildPackedResult(effectivePrNumber.Value, prFiles, budget.SafeBudgetTokens);
                    var json = JsonSerializer.Serialize(result, JsonOptions);
                    sw.Stop();
                    _logger.LogInformation("[{ToolName}] Completed (F003): PR #{PrNumber}, {FileCount} files, {ElapsedMs}ms",
                        ToolName, effectivePrNumber, prFiles.Files.Count, sw.ElapsedMilliseconds);
                    return CreateSuccessResult(json);
                }

                // Feature 004 path
                var fileItems = BuildFileItems(prFiles);
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

                StalenessWarningResult? staleness = null;
                if (metadataTask != null)
                {
                    var metadata = await metadataTask;
                    staleness = PaginationOrchestrator.CheckStaleness(
                        resolution.Fingerprint, metadata.LastMergeSourceCommitId, isPageRefMode);
                }

                string? currentFingerprint = resolution.Fingerprint;
                if (currentFingerprint == null)
                {
                    try
                    {
                        var meta = await _filesProvider.GetMetadataAsync(effectivePrNumber.Value, cancellationToken);
                        currentFingerprint = meta.LastMergeSourceCommitId;
                    }
                    catch
                    {
                        _logger.LogDebug("[{ToolName}] Could not fetch metadata for fingerprint", ToolName);
                    }
                }

                var requestParams = JsonDocument.Parse($"{{\"prNumber\":{effectivePrNumber}}}").RootElement;

                var paginationMeta = PaginationOrchestrator.BuildPaginationMetadata(
                    allocation, requestedPage, _pageReferenceCodec,
                    ToolName, requestParams, effectiveBudget, currentFingerprint);

                var manifestResult = BuildPageManifest(sortedCandidates, pageSlice, allocation, effectiveBudget);

                var paginatedResult = new PullRequestFilesResult
                {
                    PrNumber = effectivePrNumber.Value,
                    TotalFiles = prFiles.Files.Count,
                    Files = packedFiles,
                    Summary = new PullRequestFilesSummaryResult
                    {
                        SourceFiles = prFiles.Summary.SourceFiles,
                        TestFiles = prFiles.Summary.TestFiles,
                        ConfigFiles = prFiles.Summary.ConfigFiles,
                        DocsFiles = prFiles.Summary.DocsFiles,
                        BinaryFiles = prFiles.Summary.BinaryFiles,
                        GeneratedFiles = prFiles.Summary.GeneratedFiles,
                        HighPriorityFiles = prFiles.Summary.HighPriorityFiles
                    },
                    Manifest = manifestResult,
                    Pagination = paginationMeta,
                    StalenessWarning = staleness
                };

                var jsonResult = JsonSerializer.Serialize(paginatedResult, JsonOptions);
                sw.Stop();
                _logger.LogInformation(
                    "[{ToolName}] Completed (F004): PR #{PrNumber}, page {Page}/{TotalPages}, {ElapsedMs}ms",
                    ToolName, effectivePrNumber, requestedPage, allocation.TotalPages, sw.ElapsedMilliseconds);

                return CreateSuccessResult(jsonResult);
            }
            catch (PullRequestNotFoundException ex)
            {
                _logger.LogWarning(ex, "[{ToolName}] Pull request not found", ToolName);
                return CreateErrorResult($"Pull Request not found: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{ToolName}] Error", ToolName);
                return CreateErrorResult($"Error retrieving PR files: {ex.Message}");
            }
        }

        // --- Build helpers ---

        private static List<PullRequestFileItem> BuildFileItems(PullRequestFiles prFiles)
        {
            return prFiles.Files.Select(f => new PullRequestFileItem
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

        // --- F003 result builder ---

        private PullRequestFilesResult BuildPackedResult(int prNumber, PullRequestFiles prFiles, int safeBudgetTokens)
        {
            var fileItems = BuildFileItems(prFiles);
            var candidates = BuildCandidates(fileItems, safeBudgetTokens);
            var decision = _packer.Pack(candidates, safeBudgetTokens);

            var packedFiles = new List<PullRequestFileItem>();
            for (var i = 0; i < decision.Items.Count; i++)
            {
                if (decision.Items[i].Status != PackingItemStatus.Deferred)
                    packedFiles.Add(fileItems[i]);
            }

            return new PullRequestFilesResult
            {
                PrNumber = prNumber,
                TotalFiles = prFiles.Files.Count,
                Files = packedFiles,
                Summary = new PullRequestFilesSummaryResult
                {
                    SourceFiles = prFiles.Summary.SourceFiles,
                    TestFiles = prFiles.Summary.TestFiles,
                    ConfigFiles = prFiles.Summary.ConfigFiles,
                    DocsFiles = prFiles.Summary.DocsFiles,
                    BinaryFiles = prFiles.Summary.BinaryFiles,
                    GeneratedFiles = prFiles.Summary.GeneratedFiles,
                    HighPriorityFiles = prFiles.Summary.HighPriorityFiles
                },
                Manifest = MapManifest(decision.Manifest)
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

        // --- Result helpers ---

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
