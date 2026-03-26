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
    /// Handles the execution of the get_file_content_at_ref MCP tool.
    /// Validates input, delegates to <see cref="IFileContentProvider"/>,
    /// and formats the result as a structured JSON response.
    /// </summary>
    public class GetFileContentAtRefToolHandler : IMcpToolHandler
    {
        private readonly IFileContentDataProvider _fileContentProvider;
        private readonly ILogger<GetFileContentAtRefToolHandler> _logger;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public string ToolName => "get_file_content_at_ref";

        public GetFileContentAtRefToolHandler(
            IFileContentDataProvider fileContentProvider,
            ILogger<GetFileContentAtRefToolHandler> logger)
        {
            _fileContentProvider = fileContentProvider;
            _logger = logger;
        }

        public McpTool GetToolDefinition() => new()
        {
            Name = ToolName,
            Description = "Returns the full content of a file from the repository at a specific commit, branch, or tag. " +
                          "Use this to fetch the complete file without requiring a local clone or checkout. " +
                          "Typical usage: call with a commit SHA from the PR base or head to get the file before or after a change.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["path"] = new ToolProperty
                    {
                        Type = "string",
                        Description = "The repository-relative path of the file (e.g. 'src/Cache/CacheService.cs')"
                    },
                    ["ref"] = new ToolProperty
                    {
                        Type = "string",
                        Description = "The Git ref to fetch the file at: a commit SHA (e.g. 'abc123def456'), " +
                                      "a branch name (e.g. 'main'), or a tag name (e.g. 'refs/tags/v1.0')"
                    }
                },
                Required = new List<string> { "path", "ref" }
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

                if (!TryExtractRef(arguments!, out var gitRef, out error))
                {
                    _logger.LogWarning("[{ToolName}] Validation failed: {Error}", ToolName, error);
                    return CreateErrorResult(error);
                }

                _logger.LogInformation("[{ToolName}] Entry: path='{Path}', ref='{Ref}'", ToolName, path, gitRef);
                var sw = Stopwatch.StartNew();

                var fileContent = await _fileContentProvider.GetFileContentAsync(path, gitRef, cancellationToken);

                var result = new FileContentAtRefResult
                {
                    Path = fileContent.Path,
                    Ref = fileContent.Ref,
                    Size = fileContent.Size,
                    Encoding = fileContent.Encoding,
                    Content = fileContent.Content,
                    IsBinary = fileContent.IsBinary
                };

                var json = JsonSerializer.Serialize(result, JsonOptions);
                sw.Stop();

                _logger.LogInformation(
                    "[{ToolName}] Completed: path='{Path}', ref='{Ref}', {Size} bytes, binary={IsBinary}, {ResponseLength} chars, {ElapsedMs}ms",
                    ToolName, path, gitRef, fileContent.Size, fileContent.IsBinary, json.Length, sw.ElapsedMilliseconds);

                return CreateSuccessResult(json);
            }
            catch (FileContentNotFoundException ex)
            {
                _logger.LogWarning(ex, "[{ToolName}] File content not found (path='{Path}', ref='{Ref}')",
                    ToolName, arguments?.GetValueOrDefault("path"), arguments?.GetValueOrDefault("ref"));
                return CreateErrorResult($"File not found: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{ToolName}] Error (path='{Path}', ref='{Ref}')",
                    ToolName, arguments?.GetValueOrDefault("path"), arguments?.GetValueOrDefault("ref"));
                return CreateErrorResult($"Error retrieving file content: {ex.Message}");
            }
        }

        // --- Input extraction -----------------------------------------------------

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

        private static bool TryExtractRef(
            Dictionary<string, object> arguments,
            out string gitRef,
            out string errorMessage)
        {
            gitRef = string.Empty;
            errorMessage = string.Empty;

            if (!arguments.TryGetValue("ref", out var refObj))
            {
                errorMessage = "Missing required parameter: ref";
                return false;
            }

            gitRef = refObj is JsonElement jsonElement
                ? jsonElement.GetString() ?? string.Empty
                : refObj?.ToString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(gitRef))
            {
                errorMessage = "ref parameter must not be empty";
                return false;
            }

            return true;
        }

        // --- Result builders ------------------------------------------------------

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
