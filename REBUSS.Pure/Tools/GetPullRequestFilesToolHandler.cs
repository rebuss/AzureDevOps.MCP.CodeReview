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
using REBUSS.Pure.Services.Pagination;
using REBUSS.Pure.Services.ResponsePacking;
using REBUSS.Pure.Tools.Models;

namespace REBUSS.Pure.Tools
{
    /// <summary>
    /// Handles the execution of the get_pr_files MCP tool.
    /// Validates input, delegates to <see cref="IPullRequestFilesProvider"/>,
    /// and formats the result as a structured JSON response.
    /// Integrates with response packing (F003) and deterministic pagination (F004).
    /// </summary>
    [McpServerToolType]
    public class GetPullRequestFilesToolHandler
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

        [McpServerTool(Name = "get_pr_files"), Description(
            "Retrieves structured information about all files changed in a specific Pull Request. " +
            "Returns per-file metadata (status, additions, deletions, extension, " +
            "binary/generated/test flags, review priority) and an aggregated summary by category.")]
        public async Task<string> ExecuteAsync(
            [Description("The Pull Request number/ID to retrieve the file list for")] int? prNumber = null,
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

                if (prNumber != null && prNumber <= 0)
                    throw new McpException(Resources.ErrorPrNumberMustBePositive);

                if (prNumber == null && pageReference == null)
                    throw new McpException(Resources.ErrorMissingRequiredPrNumber);

                var hasExplicitBudget = modelName != null || maxTokens != null;

                _logger.LogInformation(Resources.LogGetPrFilesEntry, prNumber);
                var sw = Stopwatch.StartNew();

                var budget = _budgetResolver.Resolve(maxTokens, modelName);

                var resolution = PaginationOrchestrator.ResolvePage(
                    pageReference, pageNumber, _pageReferenceCodec, budget.SafeBudgetTokens, hasExplicitBudget);

                if (!resolution.IsSuccess)
                    throw new McpException(resolution.ErrorMessage!);

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
                            throw new McpException(paramError);
                    }
                }

                if (effectivePrNumber == null || effectivePrNumber <= 0)
                    throw new McpException(Resources.ErrorMissingRequiredPrNumber);

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
                    _logger.LogInformation(Resources.LogGetPrFilesCompletedF003,
                        effectivePrNumber, prFiles.Files.Count, sw.ElapsedMilliseconds);
                    return json;
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
                    throw new McpException(ex.Message);
                }

                var requestedPage = resolution.PageNumber;
                if (requestedPage < 1 || requestedPage > allocation.TotalPages)
                    throw new McpException(string.Format(Resources.ErrorPageNumberOutOfRange, requestedPage, allocation.TotalPages));

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
                        _logger.LogDebug(Resources.LogGetPrFilesMetadataFingerprintFailed);
                    }
                }

                var requestParams = JsonDocument.Parse($"{{\"prNumber\":{effectivePrNumber}}}").RootElement;

                var paginationMeta = PaginationOrchestrator.BuildPaginationMetadata(
                    allocation, requestedPage, _pageReferenceCodec,
                    "get_pr_files", requestParams, effectiveBudget, currentFingerprint);

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
                    Resources.LogGetPrFilesCompletedF004,
                    effectivePrNumber, requestedPage, allocation.TotalPages, sw.ElapsedMilliseconds);

                return jsonResult;
            }
            catch (PullRequestNotFoundException ex)
            {
                _logger.LogWarning(ex, Resources.LogGetPrFilesPrNotFound);
                throw new McpException(string.Format(Resources.ErrorPullRequestNotFound, ex.Message));
            }
            catch (McpException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, Resources.LogGetPrFilesError);
                throw new McpException(string.Format(Resources.ErrorRetrievingPrFiles, ex.Message));
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
    }
}
