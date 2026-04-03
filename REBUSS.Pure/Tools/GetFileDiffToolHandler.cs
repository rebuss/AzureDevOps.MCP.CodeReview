using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Properties;
using REBUSS.Pure.Tools.Models;
using REBUSS.Pure.Tools.Shared;

namespace REBUSS.Pure.Tools
{
    /// <summary>
    /// Handles the execution of the get_file_diff MCP tool.
    /// Validates input, delegates to <see cref="IPullRequestDataProvider"/>,
    /// and returns plain-text diff blocks for a single file.
    /// </summary>
    [McpServerToolType]
    public class GetFileDiffToolHandler
    {
        private readonly IPullRequestDataProvider _diffProvider;
        private readonly IResponsePacker _packer;
        private readonly IContextBudgetResolver _budgetResolver;
        private readonly ITokenEstimator _tokenEstimator;
        private readonly IFileClassifier _fileClassifier;
        private readonly ILogger<GetFileDiffToolHandler> _logger;

        public GetFileDiffToolHandler(
            IPullRequestDataProvider diffProvider,
            IResponsePacker packer,
            IContextBudgetResolver budgetResolver,
            ITokenEstimator tokenEstimator,
            IFileClassifier fileClassifier,
            ILogger<GetFileDiffToolHandler> logger)
        {
            _diffProvider = diffProvider;
            _packer = packer;
            _budgetResolver = budgetResolver;
            _tokenEstimator = tokenEstimator;
            _fileClassifier = fileClassifier;
            _logger = logger;
        }

        [McpServerTool(Name = "get_file_diff"), Description(
            "Retrieves the diff for a single file in a specific Pull Request. " +
            "Returns plain-text diff content with -/+/space prefixed lines.")]
        public async Task<IEnumerable<ContentBlock>> ExecuteAsync(
            [Description("The Pull Request number/ID to retrieve the diff for")] int? prNumber = null,
            [Description("The repository-relative path of the file (e.g. 'src/Cache/CacheService.cs')")] string? path = null,
            CancellationToken cancellationToken = default)
        {
            if (prNumber != null && prNumber <= 0)
                throw new McpException(Resources.ErrorPrNumberMustBePositive);

            if (prNumber == null)
                throw new McpException(Resources.ErrorMissingRequiredPrNumber);

            if (string.IsNullOrWhiteSpace(path))
                throw new McpException(Resources.ErrorMissingRequiredPath);

            try
            {
                _logger.LogInformation(Resources.LogGetFileDiffEntry, prNumber, path);
                var sw = Stopwatch.StartNew();

                var diff = await _diffProvider.GetFileDiffAsync(prNumber.Value, path, cancellationToken);
                var budget = _budgetResolver.Resolve(null, null);

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
                _logger.LogInformation(
                    Resources.LogGetFileDiffCompleted,
                    prNumber, path, blocks.Count, sw.ElapsedMilliseconds);

                return blocks;
            }
            catch (PullRequestNotFoundException ex)
            {
                _logger.LogWarning(ex, Resources.LogGetFileDiffPrNotFound, prNumber, path);
                throw new McpException(string.Format(Resources.ErrorPullRequestNotFound, ex.Message));
            }
            catch (FileNotFoundInPullRequestException ex)
            {
                _logger.LogWarning(ex, Resources.LogGetFileDiffFileNotFound, prNumber, path);
                throw new McpException(string.Format(Resources.ErrorFileNotFoundInPullRequest, ex.Message));
            }
            catch (McpException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, Resources.LogGetFileDiffError, prNumber, path);
                throw new McpException(string.Format(Resources.ErrorRetrievingFileDiff, ex.Message));
            }
        }
    }
}