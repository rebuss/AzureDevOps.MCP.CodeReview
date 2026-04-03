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
using REBUSS.Pure.Services.LocalReview;
using REBUSS.Pure.Services.Pagination;
using REBUSS.Pure.Services.ResponsePacking;
using REBUSS.Pure.Tools.Models;
using REBUSS.Pure.Tools.Shared;

namespace REBUSS.Pure.Tools
{
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
            "Lists all locally changed files in the git repository with classification metadata. " +
            "Returns a plain-text table of files with status, additions, deletions, and classification flags. " +
            "Scopes: '\''working-tree'\'' (default), '\''staged'\'', or a branch/ref name.")]
        public async Task<IEnumerable<ContentBlock>> ExecuteAsync(
            [Description("The change scope. '\''working-tree'\'': all uncommitted changes. '\''staged'\'': only staged. Any other value treated as base branch/ref.")] string? scope = null,
            [Description("Optional model name to resolve context window size")] string? modelName = null,
            [Description("Optional explicit context window size in tokens")] int? maxTokens = null,
            [Description("Opaque page reference from a previous response.")] string? pageReference = null,
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

                var effectiveScope = scope;
                if (resolution.DecodedParams != null)
                {
                    if (resolution.DecodedParams.Value.TryGetProperty("scope", out var decodedScope))
                        effectiveScope = decodedScope.GetString();

                    if (scope != null && effectiveScope != null)
                    {
                        var scopeJson = JsonSerializer.SerializeToElement(scope);
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
                    var blocks = BuildPackedBlocks(reviewFiles, budget.SafeBudgetTokens);
                    sw.Stop();
                    _logger.LogInformation(Resources.LogGetLocalFilesCompletedF003,
                        parsedScope, reviewFiles.Files.Count, sw.ElapsedMilliseconds);
                    return blocks;
                }

                var candidates = ToolHandlerHelpers.BuildCandidates(
                    reviewFiles.Files, effectiveBudget, _tokenEstimator, _fileClassifier,
                    fi => fi.Path, fi => fi.Additions + fi.Deletions,
                    fi => PlainTextFormatter.FormatFileEntry(fi));
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
                    reviewFiles.Files, sortedCandidates, pageSlice, fi => fi.Path);

                var scopeForRef = effectiveScope ?? "working-tree";
                var requestParams = JsonSerializer.SerializeToElement(new { scope = scopeForRef });
                var paginationMeta = PaginationOrchestrator.BuildPaginationMetadata(
                    allocation, requestedPage, _pageReferenceCodec,
                    "get_local_files", requestParams, effectiveBudget, null);
                var manifestResult = ToolHandlerHelpers.BuildPageManifest(sortedCandidates, pageSlice, allocation, effectiveBudget);

                var context = $"{parsedScope} (page {requestedPage}/{allocation.TotalPages})";
                var fileListText = PlainTextFormatter.FormatFileList(packedFiles, reviewFiles.Summary, context);

                sw.Stop();
                _logger.LogInformation(Resources.LogGetLocalFilesCompletedF004,
                    parsedScope, requestedPage, allocation.TotalPages, sw.ElapsedMilliseconds);

                return
                [
                    new TextContentBlock { Text = fileListText },
                    new TextContentBlock { Text = PlainTextFormatter.FormatManifestBlock(manifestResult) },
                    new TextContentBlock { Text = PlainTextFormatter.FormatPaginationBlock(paginationMeta) }
                ];
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

        private IEnumerable<ContentBlock> BuildPackedBlocks(LocalReviewFiles reviewFiles, int safeBudgetTokens)
        {
            var candidates = ToolHandlerHelpers.BuildCandidates(
                reviewFiles.Files, safeBudgetTokens, _tokenEstimator, _fileClassifier,
                fi => fi.Path, fi => fi.Additions + fi.Deletions,
                fi => PlainTextFormatter.FormatFileEntry(fi));
            var decision = _packer.Pack(candidates, safeBudgetTokens);

            var packedFiles = new List<PullRequestFileInfo>();
            for (var i = 0; i < decision.Items.Count; i++)
            {
                if (decision.Items[i].Status != PackingItemStatus.Deferred)
                    packedFiles.Add(reviewFiles.Files[i]);
            }

            var fileListText = PlainTextFormatter.FormatFileList(
                packedFiles, reviewFiles.Summary,
                $"{reviewFiles.Scope} (repo: {reviewFiles.RepositoryRoot})");
            return
            [
                new TextContentBlock { Text = fileListText },
                new TextContentBlock { Text = PlainTextFormatter.FormatManifestBlock(ContentManifestResult.From(decision.Manifest)) }
            ];
        }
    }
}