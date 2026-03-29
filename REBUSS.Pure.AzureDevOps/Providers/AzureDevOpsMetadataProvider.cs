using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.AzureDevOps.Api;
using REBUSS.Pure.AzureDevOps.Parsers;
using REBUSS.Pure.AzureDevOps.Properties;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.AzureDevOps.Providers
{
    /// <summary>
    /// Fetches full pull request metadata from Azure DevOps by combining data from
    /// the PR details, iterations, iteration changes, and commits endpoints.
    /// </summary>
    public class AzureDevOpsMetadataProvider
    {
        private readonly IAzureDevOpsApiClient _apiClient;
        private readonly IPullRequestMetadataParser _metadataParser;
        private readonly IIterationInfoParser _iterationParser;
        private readonly IFileChangesParser _changesParser;
        private readonly ILogger<AzureDevOpsMetadataProvider> _logger;

        public AzureDevOpsMetadataProvider(
            IAzureDevOpsApiClient apiClient,
            IPullRequestMetadataParser metadataParser,
            IIterationInfoParser iterationParser,
            IFileChangesParser changesParser,
            ILogger<AzureDevOpsMetadataProvider> logger)
        {
            _apiClient = apiClient;
            _metadataParser = metadataParser;
            _iterationParser = iterationParser;
            _changesParser = changesParser;
            _logger = logger;
        }

        public virtual async Task<FullPullRequestMetadata> GetMetadataAsync(int prNumber, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Fetching metadata for PR #{PrNumber}", prNumber);
                var sw = Stopwatch.StartNew();

                var prDetailsJson = await _apiClient.GetPullRequestDetailsAsync(prNumber);
                var metadata = _metadataParser.ParseFull(prDetailsJson);

                var iterationsJson = await _apiClient.GetPullRequestIterationsAsync(prNumber);
                var iteration = _iterationParser.ParseLast(iterationsJson);

                if (string.IsNullOrEmpty(metadata.LastMergeTargetCommitId))
                    metadata.LastMergeTargetCommitId = iteration.BaseCommit;
                if (string.IsNullOrEmpty(metadata.LastMergeSourceCommitId))
                    metadata.LastMergeSourceCommitId = iteration.TargetCommit;

                var commitsJson = await _apiClient.GetPullRequestCommitsAsync(prNumber);
                metadata.CommitShas = ParseCommitShas(commitsJson);

                if (iteration.Id > 0)
                {
                    var changesJson = await _apiClient.GetPullRequestIterationChangesAsync(prNumber, iteration.Id);
                    var files = _changesParser.Parse(changesJson);
                    metadata.ChangedFilesCount = files.Count;
                }

                sw.Stop();

                _logger.LogInformation(
                    "Metadata for PR #{PrNumber} completed: title='{Title}', status={Status}, " +
                    "{CommitCount} commit(s), {FileCount} file(s) changed, {ElapsedMs}ms",
                    prNumber, metadata.Title, metadata.Status,
                    metadata.CommitShas.Count, metadata.ChangedFilesCount, sw.ElapsedMilliseconds);

                return metadata;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning("Pull Request #{PrNumber} not found", prNumber);
                throw new PullRequestNotFoundException(string.Format(Resources.ErrorPullRequestNotFound, prNumber), ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching metadata for PR #{PrNumber}", prNumber);
                throw;
            }
        }

        private List<string> ParseCommitShas(string json)
        {
            var shas = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("value", out var values) &&
                    values.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in values.EnumerateArray())
                    {
                        if (item.TryGetProperty("commitId", out var commitId) &&
                            commitId.ValueKind == JsonValueKind.String)
                        {
                            var sha = commitId.GetString();
                            if (!string.IsNullOrEmpty(sha))
                                shas.Add(sha);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing commits JSON");
            }
            return shas;
        }
    }
}
