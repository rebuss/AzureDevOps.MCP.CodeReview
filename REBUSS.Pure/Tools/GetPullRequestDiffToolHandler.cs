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
    /// Handles the execution of the get_pr_diff MCP tool.
    /// Validates input, delegates to <see cref="IPullRequestDiffProvider"/>,
    /// and returns a structured JSON result with per-file hunks.
    /// Integrates with response packing (F003) and deterministic pagination (F004).
    /// </summary>
    [McpServerToolType]
    public class GetPullRequestDiffToolHandler
    {
        private readonly IPullRequestDataProvider _diffProvider;
        private readonly IResponsePacker _packer;
        private readonly IContextBudgetResolver _budgetResolver;
        private readonly ITokenEstimator _tokenEstimator;
        private readonly IFileClassifier _fileClassifier;
        private readonly IPageAllocator _pageAllocator;
        private readonly IPageReferenceCodec _pageReferenceCodec;
        private readonly ILogger<GetPullRequestDiffToolHandler> _logger;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public GetPullRequestDiffToolHandler(
            IPullRequestDataProvider diffProvider,
            IResponsePacker packer,
            IContextBudgetResolver budgetResolver,
            ITokenEstimator tokenEstimator,
            IFileClassifier fileClassifier,
            IPageAllocator pageAllocator,
            IPageReferenceCodec pageReferenceCodec,
            ILogger<GetPullRequestDiffToolHandler> logger)
        {
            _diffProvider = diffProvider;
            _packer = packer;
            _budgetResolver = budgetResolver;
            _tokenEstimator = tokenEstimator;
            _fileClassifier = fileClassifier;
            _pageAllocator = pageAllocator;
            _pageReferenceCodec = pageReferenceCodec;
            _logger = logger;
        }

        [McpServerTool(Name = "get_pr_diff"), Description(
            "Retrieves the diff (file changes) for a specific Pull Request. " +
            "Returns a structured JSON object with per-file hunks optimized for AI code review.")]
        public async Task<string> ExecuteAsync(
            [Description("The Pull Request number/ID to retrieve the diff for")] int? prNumber = null,
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

                _logger.LogInformation(Resources.LogGetPrDiffEntry,
                    prNumber, pageReference != null, pageNumber);
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
                    metadataTask = _diffProvider.GetMetadataAsync(effectivePrNumber.Value, cancellationToken);

                var diff = await _diffProvider.GetDiffAsync(effectivePrNumber.Value, cancellationToken);

                if (!hasExplicitBudget && pageReference == null)
                {
                    var result = BuildPackedResult(effectivePrNumber.Value, diff, budget.SafeBudgetTokens);
                    sw.Stop();
                    _logger.LogInformation(Resources.LogGetPrDiffCompletedF003,
                        effectivePrNumber, diff.Files.Count, sw.ElapsedMilliseconds);
                    return result;
                }

                var fileChanges = BuildFileChanges(diff);
                var candidates = BuildCandidates(fileChanges, effectiveBudget);
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

                var packedFiles = ExtractPageFiles(fileChanges, sortedCandidates, pageSlice, effectiveBudget);

                StalenessWarningResult? staleness = null;
                if (metadataTask != null)
                {
                    var metadata = await metadataTask;
                    staleness = PaginationOrchestrator.CheckStaleness(
                        resolution.Fingerprint, metadata.LastMergeSourceCommitId, isPageRefMode);
                }

                string? currentFingerprint = resolution.Fingerprint;
                if (currentFingerprint == null && !isPageRefMode)
                {
                    try
                    {
                        var meta = await _diffProvider.GetMetadataAsync(effectivePrNumber.Value, cancellationToken);
                        currentFingerprint = meta.LastMergeSourceCommitId;
                    }
                    catch
                    {
                        _logger.LogDebug(Resources.LogGetPrDiffMetadataFingerprintFailed);
                    }
                }

                var requestParams = JsonDocument.Parse($"{{\"prNumber\":{effectivePrNumber}}}").RootElement;

                var paginationMeta = PaginationOrchestrator.BuildPaginationMetadata(
                    allocation, requestedPage, _pageReferenceCodec,
                    "get_pr_diff", requestParams, effectiveBudget, currentFingerprint);

                var manifestResult = BuildPageManifest(sortedCandidates, pageSlice, allocation, effectiveBudget);

                var structured = new StructuredDiffResult
                {
                    PrNumber = effectivePrNumber,
                    Files = packedFiles,
                    Manifest = manifestResult,
                    Pagination = paginationMeta,
                    StalenessWarning = staleness
                };

                sw.Stop();
                _logger.LogInformation(
                    Resources.LogGetPrDiffCompletedF004,
                    effectivePrNumber, requestedPage, allocation.TotalPages, packedFiles.Count, sw.ElapsedMilliseconds);

                return JsonSerializer.Serialize(structured, JsonOptions);
            }
            catch (PullRequestNotFoundException ex)
            {
                _logger.LogWarning(ex, Resources.LogGetPrDiffPrNotFound);
                throw new McpException(string.Format(Resources.ErrorPullRequestNotFound, ex.Message));
            }
            catch (McpException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, Resources.LogGetPrDiffError);
                throw new McpException(string.Format(Resources.ErrorRetrievingPrDiff, ex.Message));
            }
        }

        // --- File changes ---

        private static List<StructuredFileChange> BuildFileChanges(PullRequestDiff diff)
        {
            return diff.Files.Select(f => new StructuredFileChange
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

        private List<PackingCandidate> BuildCandidates(List<StructuredFileChange> fileChanges, int safeBudgetTokens)
        {
            var candidates = new List<PackingCandidate>(fileChanges.Count);
            for (var i = 0; i < fileChanges.Count; i++)
            {
                var fc = fileChanges[i];
                var serialized = JsonSerializer.Serialize(fc, JsonOptions);
                var estimation = _tokenEstimator.Estimate(serialized, safeBudgetTokens);
                var classification = _fileClassifier.Classify(fc.Path);

                candidates.Add(new PackingCandidate(
                    fc.Path,
                    estimation.EstimatedTokens,
                    classification.Category,
                    fc.Additions + fc.Deletions));
            }
            return candidates;
        }

        private static List<PackingCandidate> SortCandidates(List<PackingCandidate> candidates)
        {
            var sorted = new List<PackingCandidate>(candidates);
            sorted.Sort(PackingPriorityComparer.Instance);
            return sorted;
        }

        // --- Page extraction ---

        private List<StructuredFileChange> ExtractPageFiles(
            List<StructuredFileChange> allFiles,
            List<PackingCandidate> candidates,
            PageSlice pageSlice,
            int safeBudgetTokens)
        {
            var pageFiles = new List<StructuredFileChange>();
            foreach (var item in pageSlice.Items)
            {
                var candidate = candidates[item.OriginalIndex];
                var fileChange = allFiles.FirstOrDefault(f => f.Path == candidate.Path);
                if (fileChange == null) continue;

                if (item.Status == PackingItemStatus.Partial)
                {
                    pageFiles.Add(TruncateHunks(fileChange, item.BudgetForPartial ?? 0, safeBudgetTokens));
                }
                else
                {
                    pageFiles.Add(fileChange);
                }
            }
            return pageFiles;
        }

        // --- Manifest builders ---

        private ContentManifestResult BuildPageManifest(
            List<PackingCandidate> candidates,
            PageSlice pageSlice,
            PageAllocation allocation,
            int safeBudgetTokens)
        {
            var entries = new List<ManifestEntryResult>();
            foreach (var item in pageSlice.Items)
            {
                var candidate = candidates[item.OriginalIndex];
                entries.Add(new ManifestEntryResult
                {
                    Path = candidate.Path,
                    EstimatedTokens = item.EstimatedTokens,
                    Status = item.Status.ToString(),
                    PriorityTier = candidate.Category.ToString()
                });
            }

            var summary = PaginationOrchestrator.BuildExtendedManifestSummary(
                pageSlice, allocation, safeBudgetTokens);

            return new ContentManifestResult
            {
                Items = entries,
                Summary = summary
            };
        }

        // --- Result builders (F003 path) ---

        private string BuildPackedResult(int prNumber, PullRequestDiff diff, int safeBudgetTokens)
        {
            var fileChanges = BuildFileChanges(diff);
            var candidates = BuildCandidates(fileChanges, safeBudgetTokens);

            var decision = _packer.Pack(candidates, safeBudgetTokens);

            var packedFiles = new List<StructuredFileChange>();
            for (var i = 0; i < decision.Items.Count; i++)
            {
                var item = decision.Items[i];
                switch (item.Status)
                {
                    case PackingItemStatus.Included:
                        packedFiles.Add(fileChanges[i]);
                        break;

                    case PackingItemStatus.Partial:
                        packedFiles.Add(TruncateHunks(fileChanges[i], item.BudgetForPartial ?? 0, safeBudgetTokens));
                        break;
                }
            }

            var structured = new StructuredDiffResult
            {
                PrNumber = prNumber,
                Files = packedFiles,
                Manifest = MapManifest(decision.Manifest)
            };

            return JsonSerializer.Serialize(structured, JsonOptions);
        }

        private StructuredFileChange TruncateHunks(StructuredFileChange file, int budgetForPartial, int safeBudgetTokens)
        {
            var truncated = new StructuredFileChange
            {
                Path = file.Path,
                ChangeType = file.ChangeType,
                SkipReason = file.SkipReason,
                Additions = file.Additions,
                Deletions = file.Deletions,
                Hunks = new List<StructuredHunk>()
            };

            var usedTokens = 0;
            foreach (var hunk in file.Hunks)
            {
                var serialized = JsonSerializer.Serialize(hunk, JsonOptions);
                var estimation = _tokenEstimator.Estimate(serialized, safeBudgetTokens);

                if (usedTokens + estimation.EstimatedTokens > budgetForPartial)
                    break;

                truncated.Hunks.Add(hunk);
                usedTokens += estimation.EstimatedTokens;
            }

            if (truncated.Hunks.Count < file.Hunks.Count)
            {
                truncated.SkipReason = string.Format(Resources.ErrorPartiallyIncludedHunks, truncated.Hunks.Count, file.Hunks.Count);
            }

            return truncated;
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
