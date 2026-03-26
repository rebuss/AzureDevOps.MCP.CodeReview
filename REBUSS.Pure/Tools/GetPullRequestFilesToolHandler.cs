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
    /// Handles the execution of the get_pr_files MCP tool.
    /// Validates input, delegates to <see cref="IPullRequestFilesProvider"/>,
    /// and formats the result as a structured JSON response.
    /// </summary>
    public class GetPullRequestFilesToolHandler : IMcpToolHandler
    {
        private readonly IPullRequestDataProvider _filesProvider;
        private readonly ILogger<GetPullRequestFilesToolHandler> _logger;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public string ToolName => "get_pr_files";

        public GetPullRequestFilesToolHandler(
            IPullRequestDataProvider filesProvider,
            ILogger<GetPullRequestFilesToolHandler> logger)
        {
            _filesProvider = filesProvider;
            _logger = logger;
        }

        public McpTool GetToolDefinition() => new()
        {
            Name = ToolName,
            Description = "Retrieves structured information about all files changed in a specific Pull Request. " +
                          "Returns per-file metadata (status, additions, deletions, extension, " +
                          "binary/generated/test flags, review priority) and an aggregated summary by category.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["prNumber"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "The Pull Request number/ID to retrieve the file list for"
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

                var prFiles = await _filesProvider.GetFilesAsync(prNumber, cancellationToken);
                var result = BuildResult(prNumber, prFiles);

                var json = JsonSerializer.Serialize(result, JsonOptions);
                sw.Stop();

                _logger.LogInformation("[{ToolName}] Completed: PR #{PrNumber}, {FileCount} file(s), {ResponseLength} chars, {ElapsedMs}ms",
                    ToolName, prNumber, prFiles.Files.Count, json.Length, sw.ElapsedMilliseconds);

                return CreateSuccessResult(json);
            }
            catch (PullRequestNotFoundException ex)
            {
                _logger.LogWarning(ex, "[{ToolName}] Pull request not found (prNumber={PrNumber})", ToolName, arguments?.GetValueOrDefault("prNumber"));
                return CreateErrorResult($"Pull Request not found: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{ToolName}] Error (prNumber={PrNumber})", ToolName, arguments?.GetValueOrDefault("prNumber"));
                return CreateErrorResult($"Error retrieving PR files: {ex.Message}");
            }
        }

        // --- Result builder -------------------------------------------------------

        private static PullRequestFilesResult BuildResult(int prNumber, PullRequestFiles prFiles)
        {
            return new PullRequestFilesResult
            {
                PrNumber = prNumber,
                TotalFiles = prFiles.Files.Count,
                Files = prFiles.Files.Select(f => new PullRequestFileItem
                {
                    Path = f.Path,
                    Status = f.Status,
                    Additions = f.Additions,
                    Deletions = f.Deletions,
                    Changes = f.Changes,
                    Extension = f.Extension,
                    IsBinary = f.IsBinary,
                    IsGenerated = f.IsGenerated,
                    IsTestFile = f.IsTestFile,
                    ReviewPriority = f.ReviewPriority
                }).ToList(),
                Summary = new PullRequestFilesSummaryResult
                {
                    SourceFiles = prFiles.Summary.SourceFiles,
                    TestFiles = prFiles.Summary.TestFiles,
                    ConfigFiles = prFiles.Summary.ConfigFiles,
                    DocsFiles = prFiles.Summary.DocsFiles,
                    BinaryFiles = prFiles.Summary.BinaryFiles,
                    GeneratedFiles = prFiles.Summary.GeneratedFiles,
                    HighPriorityFiles = prFiles.Summary.HighPriorityFiles
                }
            };
        }

        // --- Input extraction -----------------------------------------------------

        private static bool TryExtractPrNumber(
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

        // --- Result helpers -------------------------------------------------------

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
