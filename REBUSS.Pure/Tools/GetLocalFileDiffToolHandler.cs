using System.Diagnostics;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Mcp;
using REBUSS.Pure.Mcp.Models;
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
    public class GetLocalFileDiffToolHandler : IMcpToolHandler
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

        public string ToolName => "get_local_file_diff";

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

        public McpTool GetToolDefinition() => new()
        {
            Name = ToolName,
            Description =
                "Returns a structured diff for a single locally changed file. " +
                "Call get_local_files first to discover which files changed, then call this tool " +
                "for files you want to inspect in detail. " +
                "Supported scopes match get_local_files: 'working-tree' (default), 'staged', or a base branch/ref.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["path"] = new ToolProperty
                    {
                        Type = "string",
                        Description = "Repository-relative path of the file to diff (e.g. 'src/Service.cs')"
                    },
                    ["scope"] = new ToolProperty
                    {
                        Type = "string",
                        Description =
                            "The change scope. " +
                            "'working-tree' (default): all uncommitted changes vs HEAD. " +
                            "'staged': only staged changes vs HEAD. " +
                            "Any other value is treated as a base branch/ref."
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
                    }
                },
                Required = new List<string> { "path" }
            }
        };

        public async Task<ToolResult> ExecuteAsync(
            Dictionary<string, object>? arguments,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (!TryExtractPath(arguments, out var path, out var error))
                {
                    _logger.LogWarning("[{ToolName}] Validation failed: {Error}", ToolName, error);
                    return CreateErrorResult(error);
                }

                var scopeStr = ExtractScope(arguments!);
                var scope = LocalReviewScope.Parse(scopeStr);
                var modelName = ExtractOptionalString(arguments!, "modelName");
                var maxTokens = ExtractOptionalInt(arguments!, "maxTokens");

                _logger.LogInformation("[{ToolName}] Entry: path='{Path}', scope={Scope}",
                    ToolName, path, scope);
                var sw = Stopwatch.StartNew();

                var diff = await _reviewProvider.GetFileDiffAsync(path, scope, cancellationToken);

                var budget = _budgetResolver.Resolve(maxTokens, modelName);
                var result = BuildPackedResult(diff, budget.SafeBudgetTokens);

                var json = JsonSerializer.Serialize(result, JsonOptions);
                sw.Stop();

                _logger.LogInformation(
                    "[{ToolName}] Completed: path='{Path}', scope={Scope}, {ResponseLength} chars, {ElapsedMs}ms",
                    ToolName, path, scope, json.Length, sw.ElapsedMilliseconds);

                return CreateSuccessResult(json);
            }
            catch (LocalRepositoryNotFoundException ex)
            {
                _logger.LogWarning(ex, "[{ToolName}] Repository not found", ToolName);
                return CreateErrorResult($"Repository not found: {ex.Message}");
            }
            catch (LocalFileNotFoundException ex)
            {
                _logger.LogWarning(ex, "[{ToolName}] File not found in local changes (path='{Path}')",
                    ToolName, arguments?.GetValueOrDefault("path"));
                return CreateErrorResult($"File not found in local changes: {ex.Message}");
            }
            catch (GitCommandException ex)
            {
                _logger.LogWarning(ex, "[{ToolName}] Git command failed (path='{Path}')",
                    ToolName, arguments?.GetValueOrDefault("path"));
                return CreateErrorResult($"Git command failed: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{ToolName}] Error (path='{Path}')",
                    ToolName, arguments?.GetValueOrDefault("path"));
                return CreateErrorResult($"Error retrieving local file diff: {ex.Message}");
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
                Manifest = MapManifest(decision.Manifest)
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
                truncated.SkipReason = $"Partially included: {truncated.Hunks.Count}/{file.Hunks.Count} hunks fit within budget";
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

        private static bool TryExtractPath(
            Dictionary<string, object>? arguments,
            out string path,
            out string errorMessage)
        {
            path = string.Empty;
            errorMessage = string.Empty;

            if (arguments == null || !arguments.TryGetValue("path", out var pathObj))
            {
                errorMessage = "Missing required parameter: path";
                return false;
            }

            path = pathObj is JsonElement jsonElement
                ? jsonElement.GetString() ?? string.Empty
                : pathObj?.ToString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(path))
            {
                errorMessage = "path parameter must not be empty";
                return false;
            }

            return true;
        }

        private static string? ExtractScope(Dictionary<string, object> arguments)
        {
            if (!arguments.TryGetValue("scope", out var scopeObj))
                return null;

            return scopeObj is JsonElement jsonElement
                ? jsonElement.GetString()
                : scopeObj?.ToString();
        }

        private static string? ExtractOptionalString(Dictionary<string, object> arguments, string key)
        {
            if (!arguments.TryGetValue(key, out var value)) return null;
            return value is JsonElement jsonElement ? jsonElement.GetString() : value?.ToString();
        }

        private static int? ExtractOptionalInt(Dictionary<string, object> arguments, string key)
        {
            if (!arguments.TryGetValue(key, out var value)) return null;
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
