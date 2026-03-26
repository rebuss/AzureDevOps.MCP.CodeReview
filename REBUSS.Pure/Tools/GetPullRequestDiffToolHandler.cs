using System.Diagnostics;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Core.Models;
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
    /// </summary>
    public class GetPullRequestDiffToolHandler : IMcpToolHandler
    {
        private readonly IPullRequestDataProvider _diffProvider;
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
            ILogger<GetPullRequestDiffToolHandler> logger)
        {
            _diffProvider = diffProvider;
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

                _logger.LogInformation("[{ToolName}] Entry: PR #{PrNumber}", ToolName, prNumber);
                var sw = Stopwatch.StartNew();

                var diff = await _diffProvider.GetDiffAsync(prNumber, cancellationToken);

                var result = BuildStructuredResult(prNumber, diff);

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

        // --- Result builders ------------------------------------------------------

        private static ToolResult BuildStructuredResult(int prNumber, PullRequestDiff diff)
        {
            var structured = new StructuredDiffResult
            {
                PrNumber = prNumber,
                Files = diff.Files.Select(f => new StructuredFileChange
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
                }).ToList()
            };

            return CreateSuccessResult(JsonSerializer.Serialize(structured, JsonOptions));
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
