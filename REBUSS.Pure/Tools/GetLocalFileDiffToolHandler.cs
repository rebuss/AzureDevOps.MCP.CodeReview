using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Properties;
using REBUSS.Pure.Services.LocalReview;
using REBUSS.Pure.Tools.Models;
using REBUSS.Pure.Tools.Shared;

namespace REBUSS.Pure.Tools
{
    [McpServerToolType]
    public class GetLocalFileDiffToolHandler
    {
        private readonly ILocalReviewProvider _reviewProvider;
        private readonly IResponsePacker _packer;
        private readonly IContextBudgetResolver _budgetResolver;
        private readonly ITokenEstimator _tokenEstimator;
        private readonly IFileClassifier _fileClassifier;
        private readonly ILogger<GetLocalFileDiffToolHandler> _logger;

        public GetLocalFileDiffToolHandler(
            ILocalReviewProvider reviewProvider,
            IResponsePacker packer,
            IContextBudgetResolver budgetResolver,
            ITokenEstimator tokenEstimator,
            IFileClassifier fileClassifier,
            ILogger<GetLocalFileDiffToolHandler> logger)
        {
            _reviewProvider = reviewProvider;
            _packer = packer;
            _budgetResolver = budgetResolver;
            _tokenEstimator = tokenEstimator;
            _fileClassifier = fileClassifier;
            _logger = logger;
        }

        [McpServerTool(Name = "get_local_file_diff"), Description(
            "Returns a plain-text diff for a single locally changed file with -/+/space prefixed lines. " +
            "Call get_local_files first to discover which files changed. " +
            "Scopes: '\''working-tree'\'' (default), '\''staged'\'', or a base branch/ref.")]
        public async Task<IEnumerable<ContentBlock>> ExecuteAsync(
            [Description("Repository-relative path of the file to diff (e.g. '\''src/Service.cs'\'')")] string? path = null,
            [Description("The change scope. '\''working-tree'\'': all uncommitted. '\''staged'\'': only staged. Any other value is treated as base branch/ref.")] string? scope = null,
            [Description("Optional model name to resolve context window size")] string? modelName = null,
            [Description("Optional explicit context window size in tokens")] int? maxTokens = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new McpException(Resources.ErrorMissingRequiredPath);

            try
            {
                var parsedScope = LocalReviewScope.Parse(scope);
                _logger.LogInformation(Resources.LogGetLocalFileDiffEntry, path, parsedScope);
                var sw = Stopwatch.StartNew();

                var diff = await _reviewProvider.GetFileDiffAsync(path, parsedScope, cancellationToken);
                var budget = _budgetResolver.Resolve(maxTokens, modelName);

                var fileChanges = diff.Files.Select(f => FileTokenMeasurement.MapToStructured(f)).ToList();
                var candidates = ToolHandlerHelpers.BuildCandidates(
                    fileChanges, budget.SafeBudgetTokens, _tokenEstimator, _fileClassifier,
                    fc => fc.Path, fc => fc.Additions + fc.Deletions,
                    fc => PlainTextFormatter.FormatFileDiff(fc));
                var decision = _packer.Pack(candidates, budget.SafeBudgetTokens);

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
                            fc = ToolHandlerHelpers.TruncateHunks(fileChanges[i], item.BudgetForPartial ?? 0, budget.SafeBudgetTokens, _tokenEstimator);
                            break;
                        default:
                            continue;
                    }
                    blocks.Add(new TextContentBlock { Text = PlainTextFormatter.FormatFileDiff(fc) });
                }

                blocks.Add(new TextContentBlock { Text = PlainTextFormatter.FormatManifestBlock(ContentManifestResult.From(decision.Manifest)) });

                sw.Stop();
                _logger.LogInformation(Resources.LogGetLocalFileDiffCompleted,
                    path, parsedScope, blocks.Count, sw.ElapsedMilliseconds);

                return blocks;
            }
            catch (LocalRepositoryNotFoundException ex)
            {
                _logger.LogWarning(ex, Resources.LogGetLocalFileDiffRepositoryNotFound);
                throw new McpException(string.Format(Resources.ErrorRepositoryNotFound, ex.Message));
            }
            catch (LocalFileNotFoundException ex)
            {
                _logger.LogWarning(ex, Resources.LogGetLocalFileDiffFileNotFound, path);
                throw new McpException(string.Format(Resources.ErrorFileNotFoundInLocalChanges, ex.Message));
            }
            catch (GitCommandException ex)
            {
                _logger.LogWarning(ex, Resources.LogGetLocalFileDiffGitCommandFailed, path);
                throw new McpException(string.Format(Resources.ErrorGitCommandFailed, ex.Message));
            }
            catch (McpException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, Resources.LogGetLocalFileDiffError, path);
                throw new McpException(string.Format(Resources.ErrorRetrievingLocalFileDiff, ex.Message));
            }
        }
    }
}