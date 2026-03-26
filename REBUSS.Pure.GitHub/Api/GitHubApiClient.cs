using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using REBUSS.Pure.GitHub.Configuration;

namespace REBUSS.Pure.GitHub.Api;

/// <summary>
/// HTTP client for GitHub REST API v3.
/// Expects a pre-configured HttpClient (auth header) injected via IHttpClientFactory.
/// Base URL: <c>https://api.github.com/</c>.
/// </summary>
public class GitHubApiClient : IGitHubApiClient
{
    private const string BaseUrl = "https://api.github.com/";
    private const int MaxPagesPerEndpoint = 10;
    private const int DefaultPerPage = 100;

    private readonly HttpClient _httpClient;
    private readonly GitHubOptions _options;
    private readonly ILogger<GitHubApiClient> _logger;

    public GitHubApiClient(
        HttpClient httpClient,
        IOptions<GitHubOptions> options,
        ILogger<GitHubApiClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(BaseUrl);
    }

    public async Task<string> GetPullRequestDetailsAsync(int pullRequestNumber, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("API call: GetPullRequestDetails for PR #{PullRequestNumber}", pullRequestNumber);

        var url = $"repos/{_options.Owner}/{_options.RepositoryName}/pulls/{pullRequestNumber}";
        return await GetStringAsync(url, "GetPullRequestDetails", cancellationToken);
    }

    public async Task<string> GetPullRequestFilesAsync(int pullRequestNumber, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("API call: GetPullRequestFiles for PR #{PullRequestNumber}", pullRequestNumber);

        var url = $"repos/{_options.Owner}/{_options.RepositoryName}/pulls/{pullRequestNumber}/files";
        return await GetPaginatedArrayAsync(url, "GetPullRequestFiles", cancellationToken);
    }

    public async Task<string> GetPullRequestCommitsAsync(int pullRequestNumber, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("API call: GetPullRequestCommits for PR #{PullRequestNumber}", pullRequestNumber);

        var url = $"repos/{_options.Owner}/{_options.RepositoryName}/pulls/{pullRequestNumber}/commits";
        return await GetPaginatedArrayAsync(url, "GetPullRequestCommits", cancellationToken);
    }

    public async Task<string?> GetFileContentAtRefAsync(string gitRef, string filePath, CancellationToken cancellationToken = default)
    {
        var encodedPath = Uri.EscapeDataString(filePath).Replace("%2F", "/");
        var url = $"repos/{_options.Owner}/{_options.RepositoryName}/contents/{encodedPath}?ref={Uri.EscapeDataString(gitRef)}";

        _logger.LogDebug("Fetching file content for {FilePath} at ref {GitRef}", filePath, gitRef);

        var sw = Stopwatch.StartNew();

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));

        var response = await _httpClient.SendAsync(request, cancellationToken);
        sw.Stop();

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogDebug(
                "File {FilePath} not found at ref {GitRef} [{StatusCode}, {ElapsedMs}ms]",
                filePath, gitRef, (int)response.StatusCode, sw.ElapsedMilliseconds);
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "Contents API returned {StatusCode} for {FilePath}@{GitRef} in {ElapsedMs}ms: {Error}",
                    (int)response.StatusCode, filePath, gitRef, sw.ElapsedMilliseconds, error);
                response.EnsureSuccessStatusCode();
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);

        _logger.LogDebug(
            "GetFileContentAtRef {FilePath}@{GitRef} completed: {StatusCode}, {ResponseLength} chars, {ElapsedMs}ms",
            filePath, gitRef, (int)response.StatusCode, content.Length, sw.ElapsedMilliseconds);

        return content;
    }

    private async Task<string> GetStringAsync(string url, string operationName, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var response = await _httpClient.GetAsync(url, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        sw.Stop();

        _logger.LogDebug(
            "{Operation} completed: {StatusCode}, {ResponseLength} chars, {ElapsedMs}ms",
            operationName, (int)response.StatusCode, body.Length, sw.ElapsedMilliseconds);

        response.EnsureSuccessStatusCode();
        return body;
    }

    /// <summary>
    /// Fetches a paginated GitHub API endpoint that returns a JSON array.
    /// Combines all pages into a single JSON array string.
    /// </summary>
    private async Task<string> GetPaginatedArrayAsync(string baseUrl, string operationName, CancellationToken cancellationToken)
    {
        var allItems = new List<JsonElement>();
        var page = 1;

        while (page <= MaxPagesPerEndpoint)
        {
            var separator = baseUrl.Contains('?') ? '&' : '?';
            var url = $"{baseUrl}{separator}per_page={DefaultPerPage}&page={page}";

            var sw = Stopwatch.StartNew();
            var response = await _httpClient.GetAsync(url, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            sw.Stop();

            _logger.LogDebug(
                "{Operation} page {Page} completed: {StatusCode}, {ResponseLength} chars, {ElapsedMs}ms",
                operationName, page, (int)response.StatusCode, body.Length, sw.ElapsedMilliseconds);

            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                break;

            var items = doc.RootElement.EnumerateArray().ToList();
            if (items.Count == 0)
                break;

            foreach (var item in items)
                allItems.Add(item.Clone());

            if (items.Count < DefaultPerPage)
                break;

            page++;
        }

        return JsonSerializer.Serialize(allItems);
    }
}
