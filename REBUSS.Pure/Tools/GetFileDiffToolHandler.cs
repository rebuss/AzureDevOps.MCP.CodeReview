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
    /// Handles the execution of the get_file_diff MCP tool.
    /// Validates input, delegates to <see cref="IPullRequestDiffProvider"/>,
    /// and returns a structured JSON result with per-file hunks for a single file.
    /// </summary>
    public class GetFileDiffToolHandler : IMcpToolHandler
    {
        private readonly IPullRequestDataProvider _diffProvider;
        private readonly ILogger<GetFileDiffToolHandler> _logger;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public string ToolName => "get_file_diff";

        public GetFileDiffToolHandler(
            IPullRequestDataProvider diffProvider,
            ILogger<GetFileDiffToolHandler> logger)
        {
            _diffProvider = diffProvider;
            _logger = logger;
        }

        public McpTool GetToolDefinition() => new()
        {
            Name = ToolName,
            Description = "Retrieves the diff for a single file in a specific Pull Request. " +
                          "Returns a structured JSON object with the file diff including hunks optimized for AI code review.",
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
                    ["path"] = new ToolProperty
                    {
                        Type = "string",
                        Description = "The repository-relative path of the file (e.g. 'src/Cache/CacheService.cs')"
                    }
                },
                Required = new List<string> { "prNumber", "path" }
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

                if (!TryExtractPath(arguments!, out var path, out error))
                {
                    _logger.LogWarning("[{ToolName}] Validation failed: {Error}", ToolName, error);
                    return CreateErrorResult(error);
                }

                _logger.LogInformation("[{ToolName}] Entry: PR #{PrNumber}, path='{Path}'",
                    ToolName, prNumber, path);
                var sw = Stopwatch.StartNew();

                var diff = await _diffProvider.GetFileDiffAsync(prNumber, path, cancellationToken);

                var result = BuildStructuredResult(prNumber, diff);

                sw.Stop();

                _logger.LogInformation(
                    "[{ToolName}] Completed: PR #{PrNumber}, path='{Path}', {ResponseLength} chars, {ElapsedMs}ms",
                    ToolName, prNumber, path, result.Content[0].Text.Length, sw.ElapsedMilliseconds);

                return result;
            }
            catch (PullRequestNotFoundException ex)
            {
                _logger.LogWarning(ex, "[{ToolName}] Pull request not found (prNumber={PrNumber}, path='{Path}')",
                    ToolName, arguments?.GetValueOrDefault("prNumber"), arguments?.GetValueOrDefault("path"));
                return CreateErrorResult($"Pull Request not found: {ex.Message}");
            }
            catch (FileNotFoundInPullRequestException ex)
            {
                _logger.LogWarning(ex, "[{ToolName}] File not found in pull request (prNumber={PrNumber}, path='{Path}')",
                    ToolName, arguments?.GetValueOrDefault("prNumber"), arguments?.GetValueOrDefault("path"));
                return CreateErrorResult($"File not found in Pull Request: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{ToolName}] Error (prNumber={PrNumber}, path='{Path}')",
                    ToolName, arguments?.GetValueOrDefault("prNumber"), arguments?.GetValueOrDefault("path"));
                return CreateErrorResult($"Error retrieving file diff: {ex.Message}");
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

        private static bool TryExtractPath(
            Dictionary<string, object> arguments,
            out string path,
            out string errorMessage)
        {
            path = string.Empty;
            errorMessage = string.Empty;

            if (!arguments.TryGetValue("path", out var pathObj))
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
