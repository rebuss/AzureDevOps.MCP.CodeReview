using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Properties;
using REBUSS.Pure.Services.LocalReview;
using REBUSS.Pure.Tools.Models;
using System.Text.Json;
using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.Tools
{
    /// <summary>
    /// Handles the <c>get_local_file_diff</c> MCP tool.
    /// Returns a structured diff for a single locally changed file so an AI agent
    /// can inspect the exact changes in detail.
    /// Integrates with response packing to fit results within the context budget.
    /// </summary>
    [McpServerToolType]
    public class GetLocalFileDiffToolHandler
    {
        private readonly ILocalReviewProvider _reviewProvider;
        private readonly IResponsePacker _packer;
        private readonly IContextBudgetResolver _budgetResolver;
        private readonly ITokenEstimator _tokenEstimator;
        private readonly IFileClassifier _fileClassifier;
        private readonly ILogger<GetLocalFileDiffToolHandler> _logger;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

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
            "Returns a structured diff for a single locally changed file. " +
            "Call get_local_files first to discover which files changed, then call this tool " +
            "for files you want to inspect in detail. " +
            "Supported scopes match get_local_files: 'working-tree' (default), 'staged', or a base branch/ref.")]
        public async Task<string> ExecuteAsync(
            [Description("Repository-relative path of the file to diff (e.g. 'src/Service.cs')")] string? path = null,
            [Description("The change scope. " +
                        "'working-tree' (default): all uncommitted changes vs HEAD. " +
                        "'staged': only staged changes vs HEAD. " +
                        "Any other value is treated as a base branch/ref.")] string? scope = null,
            [Description("Optional model name (e.g. 'Claude Sonnet') to resolve context window size")] string? modelName = null,
            [Description("Optional explicit context window size in tokens")] int? maxTokens = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new McpException(Resources.ErrorMissingRequiredPath);

            try
            {
                var parsedScope = LocalReviewScope.Parse(scope);

                _logger.LogInformation(Resources.LogGetLocalFileDiffEntry,
                    path, parsedScope);
                var sw = Stopwatch.StartNew();

                var diff = await _reviewProvider.GetFileDiffAsync(path, parsedScope, cancellationToken);

                var budget = _budgetResolver.Resolve(maxTokens, modelName);
                var result = BuildPackedResult(diff, budget.SafeBudgetTokens);

                var json = JsonSerializer.Serialize(result, JsonOptions);
                sw.Stop();

                _logger.LogInformation(
                    Resources.LogGetLocalFileDiffCompleted,
                    path, parsedScope, json.Length, sw.ElapsedMilliseconds);

                return json;
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
            catch (McpException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, Resources.LogGetLocalFileDiffError, path);
                throw new McpException(string.Format(Resources.ErrorRetrievingLocalFileDiff, ex.Message));
            }
        }

        private StructuredDiffResult BuildPackedResult(PullRequestDiff diff, int safeBudgetTokens)
        {
            var fileChanges = diff.Files.Select(f => new StructuredFileChange
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

            // Estimate tokens per file and build candidates
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

            var decision = _packer.Pack(candidates, safeBudgetTokens);

            // Filter files based on packing decision
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

            return new StructuredDiffResult
            {
                PrNumber = null,
                Files = packedFiles,
                Manifest = ContentManifestResult.From(decision.Manifest)
            };
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

            }
        }