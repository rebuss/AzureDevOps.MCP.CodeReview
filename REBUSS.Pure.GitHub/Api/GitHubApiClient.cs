using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using REBUSS.Pure.GitHub.Configuration;
using REBUSS.Pure.GitHub.Properties;

namespace REBUSS.Pure.GitHub.Api;

/// <summary>
/// HTTP client for GitHub REST API v3.
/// Expects a pre-configured HttpClient (auth header) injected via IHttpClientFactory.
/// Base URL: <c>https://api.github.com/</c>.
/// </summary>
public class GitHubApiClient : IGitHubApiClient
{
    private const int MaxPagesPerEndpoint = 10;
    private const int DefaultPerPage = 100;

    // Instance caches: scoped to a single DI resolution (transient client from IHttpClientFactory).
    // Deduplicates calls within one tool handler invocation; fresh data on every new resolution
    // so force-pushes are never masked by stale entries.
    private readonly ConcurrentDictionary<int, string> _prDetailsCache = new();
    private readonly ConcurrentDictionary<int, string> _prFilesCache = new();

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
            _httpClient.BaseAddress = new Uri(Resources.ApiBaseUrl);
    }

    public async Task<string> GetPullRequestDetailsAsync(int pullRequestNumber, CancellationToken cancellationToken = default)
    {
        if (_prDetailsCache.TryGetValue(pullRequestNumber, out var cached))
        {
            _logger.LogDebug("GetPullRequestDetails cache hit for PR #{PullRequestNumber}", pullRequestNumber);
            return cached;
        }

        _logger.LogDebug("API call: GetPullRequestDetails for PR #{PullRequestNumber}", pullRequestNumber);

        var url = $"repos/{_options.Owner}/{_options.RepositoryName}/pulls/{pullRequestNumber}";
        var result = await GetStringAsync(url, "GetPullRequestDetails", cancellationToken);
        _prDetailsCache.TryAdd(pullRequestNumber, result);
        return result;
    }

    public async Task<string> GetPullRequestFilesAsync(int pullRequestNumber, CancellationToken cancellationToken = default)
    {
        if (_prFilesCache.TryGetValue(pullRequestNumber, out var cached))
        {
            _logger.LogDebug("GetPullRequestFiles cache hit for PR #{PullRequestNumber}", pullRequestNumber);
            return cached;
        }

        _logger.LogDebug("API call: GetPullRequestFiles for PR #{PullRequestNumber}", pullRequestNumber);

        var url = $"repos/{_options.Owner}/{_options.RepositoryName}/pulls/{pullRequestNumber}/files";
        var result = await GetPaginatedArrayAsync(url, "GetPullRequestFiles", cancellationToken);
        _prFilesCache.TryAdd(pullRequestNumber, result);
        return result;
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
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(Resources.GitHubRawContentAcceptHeader));

        var response = await _httpClient.SendAsync(request, cancellationToken);
        sw.Stop();

        LogRateLimitHeaders(response, filePath);

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

    public async Task DownloadRepositoryZipToFileAsync(string commitRef, string destinationPath, CancellationToken cancellationToken = default)
    {
        var url = $"repos/{_options.Owner}/{_options.RepositoryName}/zipball/{Uri.EscapeDataString(commitRef)}";

        _logger.LogDebug("Downloading repository ZIP at ref {CommitRef}", commitRef);

        var sw = Stopwatch.StartNew();
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        LogRateLimitHeaders(response, "DownloadRepositoryZip");
        response.EnsureSuccessStatusCode();

        await using var httpStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await httpStream.CopyToAsync(fileStream, cancellationToken);

        sw.Stop();
        var fileSize = new FileInfo(destinationPath).Length;
        _logger.LogDebug(
            "Repository ZIP downloaded: {FileSize} bytes, {ElapsedMs}ms",
            fileSize, sw.ElapsedMilliseconds);
    }

    private void LogRateLimitHeaders(HttpResponseMessage response, string context)
    {
        var remaining = response.Headers.TryGetValues(Resources.GitHubRateLimitRemainingHeader, out var remValues)
            ? remValues.FirstOrDefault() : null;
        var limit = response.Headers.TryGetValues(Resources.GitHubRateLimitLimitHeader, out var limValues)
            ? limValues.FirstOrDefault() : null;
        var retryAfter = response.Headers.TryGetValues(Resources.GitHubRetryAfterHeader, out var raValues)
            ? raValues.FirstOrDefault() : null;

        if (retryAfter is not null)
        {
            _logger.LogWarning(
                "Rate limit hit for {Context}: Retry-After={RetryAfter}s, Remaining={Remaining}/{Limit}, Status={StatusCode}",
                context, retryAfter, remaining ?? "?", limit ?? "?", (int)response.StatusCode);
        }
        else if (remaining is not null && int.TryParse(remaining, out var rem) && rem < 100)
        {
            _logger.LogWarning(
                "Rate limit low for {Context}: Remaining={Remaining}/{Limit}",
                context, remaining, limit ?? "?");
        }
    }

    private async Task<string> GetStringAsync(string url, string operationName, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var response = await _httpClient.GetAsync(url, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        sw.Stop();

        LogRateLimitHeaders(response, operationName);

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

            LogRateLimitHeaders(response, operationName);

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
