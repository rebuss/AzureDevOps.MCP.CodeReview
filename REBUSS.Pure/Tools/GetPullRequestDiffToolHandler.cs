using System.Diagnostics;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Mcp;
using REBUSS.Pure.Mcp.Models;
using REBUSS.Pure.Tools.Models;
using System.Text.Json;

namespace REBUSS.Pure.Tools
{
    /// <summary>
    /// Handles the execution of the get_pr_diff MCP tool.
    /// Validates input, delegates to <see cref="IPullRequestDiffProvider"/>,
    /// and returns a structured JSON result with per-file hunks.
    /// Integrates with response packing to fit results within the context budget.
    /// </summary>
    public class GetPullRequestDiffToolHandler : IMcpToolHandler
    {
        private readonly IPullRequestDataProvider _diffProvider;
        private readonly IResponsePacker _packer;
        private readonly IContextBudgetResolver _budgetResolver;
        private readonly ITokenEstimator _tokenEstimator;
        private readonly IFileClassifier _fileClassifier;
        private readonly ILogger<GetPullRequestDiffToolHandler> _logger;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public string ToolName => "get_pr_diff";

        public GetPullRequestDiffToolHandler(
            IPullRequestDataProvider diffProvider,
            IResponsePacker packer,
            IContextBudgetResolver budgetResolver,
            ITokenEstimator tokenEstimator,
            IFileClassifier fileClassifier,
            ILogger<GetPullRequestDiffToolHandler> logger)
        {
            _diffProvider = diffProvider;
            _packer = packer;
            _budgetResolver = budgetResolver;
            _tokenEstimator = tokenEstimator;
            _fileClassifier = fileClassifier;
            _logger = logger;
        }

        public McpTool GetToolDefinition() => new()
        {
            Name = ToolName,
            Description = "Retrieves the diff (file changes) for a specific Pull Request. " +
                          "Returns a structured JSON object with per-file hunks optimized for AI code review.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["prNumber"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "The Pull Request number/ID to retrieve the diff for"
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
                Required = new List<string> { "prNumber" }
            }
        };

        public async Task<ToolResult> ExecuteAsync(
            Dictionary<string, object>? arguments,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (!TryExtractPrNumber(arguments, out var prNumber, out var error))
                {
                    _logger.LogWarning("[{ToolName}] Validation failed: {Error}", ToolName, error);
                    return CreateErrorResult(error);
                }

                var modelName = ExtractOptionalString(arguments!, "modelName");
                var maxTokens = ExtractOptionalInt(arguments!, "maxTokens");

                _logger.LogInformation("[{ToolName}] Entry: PR #{PrNumber}", ToolName, prNumber);
                var sw = Stopwatch.StartNew();

                var diff = await _diffProvider.GetDiffAsync(prNumber, cancellationToken);

                var budget = _budgetResolver.Resolve(maxTokens, modelName);
                var result = BuildPackedResult(prNumber, diff, budget.SafeBudgetTokens);

                sw.Stop();

                _logger.LogInformation(
                    "[{ToolName}] Completed: PR #{PrNumber}, {FileCount} file(s), {ResponseLength} chars, {ElapsedMs}ms",
                    ToolName, prNumber, diff.Files.Count, result.Content[0].Text.Length, sw.ElapsedMilliseconds);

                return result;
            }
            catch (PullRequestNotFoundException ex)
            {
                _logger.LogWarning(ex, "[{ToolName}] Pull request not found (prNumber={PrNumber})", ToolName, arguments?.GetValueOrDefault("prNumber"));
                return CreateErrorResult($"Pull Request not found: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{ToolName}] Error (prNumber={PrNumber})",
                    ToolName, arguments?.GetValueOrDefault("prNumber"));
                return CreateErrorResult($"Error retrieving PR diff: {ex.Message}");
            }
        }

        // --- Input extraction -----------------------------------------------------

        private bool TryExtractPrNumber(
            Dictionary<string, object>? arguments,
            out int prNumber,
            out string errorMessage)
        {
            prNumber = 0;
            errorMessage = string.Empty;

            if (arguments == null || !arguments.TryGetValue("prNumber", out var prNumberObj))
            {
                errorMessage = "Missing required parameter: prNumber";
                return false;
            }

            try
            {
                prNumber = prNumberObj is JsonElement jsonElement
                    ? jsonElement.GetInt32()
                    : Convert.ToInt32(prNumberObj);
            }
            catch
            {
                errorMessage = "Invalid prNumber parameter: must be an integer";
                return false;
            }

            if (prNumber <= 0)
            {
                errorMessage = "prNumber must be greater than 0";
                return false;
            }

            return true;
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

        // --- Result builders ------------------------------------------------------

        private ToolResult BuildPackedResult(int prNumber, PullRequestDiff diff, int safeBudgetTokens)
        {
            // Build structured file changes
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

            // Pack
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

                    // Deferred items are omitted from the response
                }
            }

            var structured = new StructuredDiffResult
            {
                PrNumber = prNumber,
                Files = packedFiles,
                Manifest = MapManifest(decision.Manifest)
            };

            return CreateSuccessResult(JsonSerializer.Serialize(structured, JsonOptions));
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
