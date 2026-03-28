using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Tools.Models;
using System.Text.Json;

namespace REBUSS.Pure.Tools
{
    /// <summary>
    /// Handles the execution of the get_pr_metadata MCP tool.
    /// Validates input, delegates to <see cref="IPullRequestMetadataProvider"/>,
    /// and formats the result as a structured JSON response.
    /// </summary>
    [McpServerToolType]
    public class GetPullRequestMetadataToolHandler
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

        public GetPullRequestMetadataToolHandler(
            IPullRequestDataProvider metadataProvider,
            ILogger<GetPullRequestMetadataToolHandler> logger)
        {
            _metadataProvider = metadataProvider;
            _logger = logger;
        }

        [McpServerTool(Name = "get_pr_metadata"), Description(
            "Retrieves metadata for a specific Pull Request. " +
            "Returns a JSON object with PR details including title, author, state, " +
            "branches, stats, commit SHAs, and description.")]
        public async Task<string> ExecuteAsync(
            [Description("The Pull Request number/ID to retrieve metadata for")] int? prNumber = null,
            CancellationToken cancellationToken = default)
        {
            if (prNumber != null && prNumber <= 0)
                throw new McpException("prNumber must be greater than 0");

            if (prNumber == null)
                throw new McpException("Missing required parameter: prNumber");

            try
            {
                _logger.LogInformation("[get_pr_metadata] Entry: PR #{PrNumber}", prNumber);
                var sw = Stopwatch.StartNew();

                var metadata = await _metadataProvider.GetMetadataAsync(prNumber.Value, cancellationToken);
                var result = BuildMetadataResult(prNumber.Value, metadata);

                var json = JsonSerializer.Serialize(result, JsonOptions);
                sw.Stop();

                _logger.LogInformation("[get_pr_metadata] Completed: PR #{PrNumber}, {ResponseLength} chars, {ElapsedMs}ms",
                    prNumber, json.Length, sw.ElapsedMilliseconds);

                return json;
            }
            catch (PullRequestNotFoundException ex)
            {
                _logger.LogWarning(ex, "[get_pr_metadata] Pull request not found (prNumber={PrNumber})", prNumber);
                throw new McpException($"Pull Request not found: {ex.Message}");
            }
            catch (McpException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[get_pr_metadata] Error (prNumber={PrNumber})", prNumber);
                throw new McpException($"Error retrieving PR metadata: {ex.Message}");
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
    }
}