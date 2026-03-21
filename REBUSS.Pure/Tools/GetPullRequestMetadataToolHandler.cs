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
    /// Handles the execution of the get_pr_metadata MCP tool.
    /// Validates input, delegates to <see cref="IPullRequestMetadataProvider"/>,
    /// and formats the result as a structured JSON response.
    /// </summary>
    public class GetPullRequestMetadataToolHandler : IMcpToolHandler
    {
        private readonly IPullRequestDataProvider _metadataProvider;
        private readonly ILogger<GetPullRequestMetadataToolHandler> _logger;

        private const int MaxDescriptionLength = 800;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public string ToolName => "get_pr_metadata";

        public GetPullRequestMetadataToolHandler(
            IPullRequestDataProvider metadataProvider,
            ILogger<GetPullRequestMetadataToolHandler> logger)
        {
            _metadataProvider = metadataProvider;
            _logger = logger;
        }

        public McpTool GetToolDefinition() => new()
        {
            Name = ToolName,
            Description = "Retrieves metadata for a specific Pull Request. " +
                          "Returns a JSON object with PR details including title, author, state, " +
                          "branches, stats, commit SHAs, and description.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["prNumber"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "The Pull Request number/ID to retrieve metadata for"
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

                var metadata = await _metadataProvider.GetMetadataAsync(prNumber, cancellationToken);
                var result = BuildMetadataResult(prNumber, metadata);

                var json = JsonSerializer.Serialize(result, JsonOptions);
                sw.Stop();

                _logger.LogInformation("[{ToolName}] Completed: PR #{PrNumber}, {ResponseLength} chars, {ElapsedMs}ms",
                    ToolName, prNumber, json.Length, sw.ElapsedMilliseconds);

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
                return CreateErrorResult($"Error retrieving PR metadata: {ex.Message}");
            }
        }

        // --- Result builder -------------------------------------------------------

        private PullRequestMetadataResult BuildMetadataResult(int prNumber, FullPullRequestMetadata metadata)
        {
            return new PullRequestMetadataResult
            {
                PrNumber = prNumber,
                Id = metadata.CodeReviewId,
                Title = metadata.Title,
                Author = new AuthorInfo
                {
                    Login = metadata.AuthorLogin,
                    DisplayName = metadata.AuthorDisplayName
                },
                State = metadata.Status,
                IsDraft = metadata.IsDraft,
                CreatedAt = metadata.CreatedDate.ToString("O"),
                UpdatedAt = metadata.ClosedDate?.ToString("O"),
                Base = new RefInfo
                {
                    Ref = metadata.TargetBranch,
                    Sha = metadata.LastMergeTargetCommitId
                },
                Head = new RefInfo
                {
                    Ref = metadata.SourceBranch,
                    Sha = metadata.LastMergeSourceCommitId
                },
                Stats = new PrStats
                {
                    Commits = metadata.CommitShas.Count,
                    ChangedFiles = metadata.ChangedFilesCount,
                    Additions = metadata.Additions,
                    Deletions = metadata.Deletions
                },
                CommitShas = metadata.CommitShas,
                Description = BuildDescriptionInfo(metadata.Description),
                Source = new SourceInfo
                {
                    Repository = metadata.RepositoryFullName,
                    Url = metadata.WebUrl
                }
            };
        }

        private static DescriptionInfo BuildDescriptionInfo(string description)
        {
            var originalLength = description?.Length ?? 0;
            var text = description ?? string.Empty;
            var isTruncated = originalLength > MaxDescriptionLength;

            if (isTruncated)
                text = text[..MaxDescriptionLength];

            return new DescriptionInfo
            {
                Text = text,
                IsTruncated = isTruncated,
                OriginalLength = originalLength,
                ReturnedLength = text.Length
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
