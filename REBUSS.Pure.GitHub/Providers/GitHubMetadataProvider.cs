using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.GitHub.Api;
using REBUSS.Pure.GitHub.Parsers;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.GitHub.Providers;

/// <summary>
/// Fetches full pull request metadata from GitHub by combining data from
/// the PR details and commits endpoints.
/// </summary>
public class GitHubMetadataProvider
{
    private readonly IGitHubApiClient _apiClient;
    private readonly IGitHubPullRequestParser _prParser;
    private readonly ILogger<GitHubMetadataProvider> _logger;

    public GitHubMetadataProvider(
        IGitHubApiClient apiClient,
        IGitHubPullRequestParser prParser,
        ILogger<GitHubMetadataProvider> logger)
    {
        _apiClient = apiClient;
        _prParser = prParser;
        _logger = logger;
    }

    public virtual async Task<FullPullRequestMetadata> GetMetadataAsync(int prNumber, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Fetching metadata for PR #{PrNumber}", prNumber);
            var sw = Stopwatch.StartNew();

            var prDetailsJson = await _apiClient.GetPullRequestDetailsAsync(prNumber);
            var metadata = _prParser.ParseFull(prDetailsJson);

            var commitsJson = await _apiClient.GetPullRequestCommitsAsync(prNumber);
            metadata.CommitShas = ParseCommitShas(commitsJson);

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
            throw new PullRequestNotFoundException($"Pull Request #{prNumber} not found", ex);
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

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return shas;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("sha", out var sha) &&
                    sha.ValueKind == JsonValueKind.String)
                {
                    shas.Add(sha.GetString()!);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error parsing commit SHAs from GitHub API response");
        }

        return shas;
    }
}
