using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
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
using REBUSS.Pure.Tools.Shared;

namespace REBUSS.Pure.Tools
{
    /// <summary>
    /// Handles the execution of the get_pr_diff MCP tool.
    /// Validates input, delegates to <see cref="IPullRequestDiffProvider"/>,
    /// and returns plain-text diff blocks — one per file.
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
            "Returns plain-text diff content with -/+/space prefixed lines, one content block per file.")]
        public async Task<IEnumerable<ContentBlock>> ExecuteAsync(
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
                        var prJsonElement = JsonSerializer.SerializeToElement(prNumber);
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
                    var blocks = BuildPackedBlocks(effectivePrNumber.Value, diff, budget.SafeBudgetTokens);
                    sw.Stop();
                    _logger.LogInformation(Resources.LogGetPrDiffCompletedF003,
                        effectivePrNumber, diff.Files.Count, sw.ElapsedMilliseconds);
                    return blocks;
                }

                var fileChanges = BuildFileChanges(diff);
                var candidates = ToolHandlerHelpers.BuildCandidates(
                    fileChanges, effectiveBudget, _tokenEstimator, _fileClassifier,
                    fc => fc.Path, fc => fc.Additions + fc.Deletions,
                    fc => PlainTextFormatter.FormatFileDiff(fc));
                var sortedCandidates = ToolHandlerHelpers.SortCandidates(candidates);

                PageAllocation allocation;
                try
                {
                    allocation = _pageAllocator.Allocate(sortedCandidates, effectiveBudget);
                }
                catch (BudgetTooSmallException ex)
                {
                    throw new McpException(ex.Message);
                }

                var requestedPage = resolution.PageNumber;
                if (requestedPage < 1 || requestedPage > allocation.TotalPages)
                    throw new McpException(string.Format(Resources.ErrorPageNumberOutOfRange, requestedPage, allocation.TotalPages));

                var pageSlice = allocation.Pages[requestedPage - 1];

                var packedFiles = ToolHandlerHelpers.ExtractPageFiles(
                    fileChanges, sortedCandidates, pageSlice,
                    fc => fc.Path,
                    (fc, budget) => ToolHandlerHelpers.TruncateHunks(fc, budget, effectiveBudget, _tokenEstimator));

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
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, Resources.LogGetPrDiffMetadataFingerprintFailed);
                    }
                }

                var requestParams = JsonSerializer.SerializeToElement(new { prNumber = effectivePrNumber });

                var paginationMeta = PaginationOrchestrator.BuildPaginationMetadata(
                    allocation, requestedPage, _pageReferenceCodec,
                    "get_pr_diff", requestParams, effectiveBudget, currentFingerprint);

                var manifestResult = ToolHandlerHelpers.BuildPageManifest(sortedCandidates, pageSlice, allocation, effectiveBudget);

                var blocks004 = new List<ContentBlock>(packedFiles.Count + 2);
                foreach (var f in packedFiles)
                    blocks004.Add(new TextContentBlock { Text = PlainTextFormatter.FormatFileDiff(f) });
                blocks004.Add(new TextContentBlock { Text = PlainTextFormatter.FormatManifestBlock(manifestResult) });
                blocks004.Add(new TextContentBlock { Text = PlainTextFormatter.FormatPaginationBlock(paginationMeta, staleness) });

                sw.Stop();
                _logger.LogInformation(
                    Resources.LogGetPrDiffCompletedF004,
                    effectivePrNumber, requestedPage, allocation.TotalPages, packedFiles.Count, sw.ElapsedMilliseconds);

                return blocks004;
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

        // --- F003 path ---

        private IEnumerable<ContentBlock> BuildPackedBlocks(int prNumber, PullRequestDiff diff, int safeBudgetTokens)
        {
            var fileChanges = BuildFileChanges(diff);
            var candidates = ToolHandlerHelpers.BuildCandidates(
                fileChanges, safeBudgetTokens, _tokenEstimator, _fileClassifier,
                fc => fc.Path, fc => fc.Additions + fc.Deletions,
                fc => PlainTextFormatter.FormatFileDiff(fc));

            var decision = _packer.Pack(candidates, safeBudgetTokens);

            var blocks = new List<ContentBlock>(decision.Items.Count + 1);
            for (var i = 0; i < decision.Items.Count; i++)
            {
                var item = decision.Items[i];
                StructuredFileChange fc;
                switch (item.Status)
                {
                    case PackingItemStatus.Included:
                        fc = fileChanges[i];
                        break;
                    case PackingItemStatus.Partial:
                        fc = ToolHandlerHelpers.TruncateHunks(fileChanges[i], item.BudgetForPartial ?? 0, safeBudgetTokens, _tokenEstimator);
                        break;
                    default:
                        continue;
                }
                blocks.Add(new TextContentBlock { Text = PlainTextFormatter.FormatFileDiff(fc) });
            }

            blocks.Add(new TextContentBlock { Text = PlainTextFormatter.FormatManifestBlock(ContentManifestResult.From(decision.Manifest)) });
            return blocks;
        }
    }
}
