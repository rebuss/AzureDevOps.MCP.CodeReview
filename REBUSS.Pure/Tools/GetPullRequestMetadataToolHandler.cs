using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Properties;
using REBUSS.Pure.Services.ResponsePacking;
using REBUSS.Pure.Tools.Models;
using REBUSS.Pure.Tools.Shared;
using System.Text.Json;

namespace REBUSS.Pure.Tools
{
    /// <summary>
    /// Handles the execution of the get_pr_metadata MCP tool.
    /// Validates input, delegates to <see cref="IPullRequestMetadataProvider"/>,
    /// and formats the result as a structured JSON response.
    /// When budget parameters are provided, also computes pagination info.
    /// </summary>
    [McpServerToolType]
    public class GetPullRequestMetadataToolHandler
    {
        private readonly IPullRequestDataProvider _metadataProvider;
        private readonly IContextBudgetResolver _budgetResolver;
        private readonly ITokenEstimator _tokenEstimator;
        private readonly IFileClassifier _fileClassifier;
        private readonly IPageAllocator _pageAllocator;
        private readonly IPullRequestDiffCache _diffCache;
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
            IContextBudgetResolver budgetResolver,
            ITokenEstimator tokenEstimator,
            IFileClassifier fileClassifier,
            IPageAllocator pageAllocator,
            IPullRequestDiffCache diffCache,
            ILogger<GetPullRequestMetadataToolHandler> logger)
        {
            _metadataProvider = metadataProvider;
            _budgetResolver = budgetResolver;
            _tokenEstimator = tokenEstimator;
            _fileClassifier = fileClassifier;
            _pageAllocator = pageAllocator;
            _diffCache = diffCache;
            _logger = logger;
        }

        [McpServerTool(Name = "get_pr_metadata"), Description(
            "Retrieves metadata for a specific Pull Request. " +
            "Returns a JSON object with PR details including title, author, state, " +
            "branches, stats, commit SHAs, and description. " +
            "When modelName or maxTokens is provided, also returns contentPaging " +
            "with page count and per-page file breakdown for use with get_pr_content.")]
        public async Task<string> ExecuteAsync(
            [Description("The Pull Request number/ID to retrieve metadata for")] int? prNumber = null,
            [Description("Model name for context budget resolution (e.g. 'gpt-4o'). Triggers pagination info.")] string? modelName = null,
            [Description("Explicit token budget override. Triggers pagination info.")] int? maxTokens = null,
            CancellationToken cancellationToken = default)
        {
            if (prNumber != null && prNumber <= 0)
                throw new McpException(Resources.ErrorPrNumberMustBePositive);

            if (prNumber == null)
                throw new McpException(Resources.ErrorMissingRequiredPrNumber);

            try
            {
                _logger.LogInformation(Resources.LogGetPrMetadataEntry, prNumber);
                var sw = Stopwatch.StartNew();

                var metadata = await _metadataProvider.GetMetadataAsync(prNumber.Value, cancellationToken);
                var result = BuildMetadataResult(prNumber.Value, metadata);

                // Compute pagination info when budget parameters are provided
                if (modelName != null || maxTokens != null)
                {
                    var pagingInfo = await BuildContentPagingInfoAsync(
                        prNumber.Value, metadata.LastMergeSourceCommitId, modelName, maxTokens, cancellationToken);
                    result.ContentPaging = pagingInfo;
                }

                var json = JsonSerializer.Serialize(result, JsonOptions);
                sw.Stop();

                _logger.LogInformation(Resources.LogGetPrMetadataCompleted,
                    prNumber, json.Length, sw.ElapsedMilliseconds);

                return json;
            }
            catch (PullRequestNotFoundException ex)
            {
                _logger.LogWarning(ex, Resources.LogGetPrMetadataNotFound, prNumber);
                throw new McpException(string.Format(Resources.ErrorPullRequestNotFound, ex.Message));
            }
            catch (McpException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, Resources.LogGetPrMetadataError, prNumber);
                throw new McpException(string.Format(Resources.ErrorRetrievingPrMetadata, ex.Message));
            }
        }

        // --- Pagination info builder -----------------------------------------------

        private async Task<ContentPagingInfo> BuildContentPagingInfoAsync(
            int prNumber, string? knownHeadCommitId, string? modelName, int? maxTokens, CancellationToken ct)
        {
            var diff = await _diffCache.GetOrFetchDiffAsync(prNumber, knownHeadCommitId, ct);

            var budget = _budgetResolver.Resolve(maxTokens, modelName);
            var safeBudget = budget.SafeBudgetTokens;

            var candidates = FileTokenMeasurement.BuildCandidatesFromDiff(diff, _tokenEstimator, _fileClassifier);
            candidates.Sort(PackingPriorityComparer.Instance);

            var allocation = _pageAllocator.Allocate(candidates, safeBudget);

            var filesByPage = allocation.Pages
                .Select(p => new PageFileCount(p.PageNumber, p.Items.Count))
                .ToList();

            return new ContentPagingInfo(
                TotalPages: allocation.TotalPages,
                TotalFiles: allocation.TotalItems,
                BudgetPerPageTokens: safeBudget,
                FilesByPage: filesByPage);
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