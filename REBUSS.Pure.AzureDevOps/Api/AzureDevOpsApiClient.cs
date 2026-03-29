using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using REBUSS.Pure.AzureDevOps;
using REBUSS.Pure.AzureDevOps.Configuration;
using REBUSS.Pure.AzureDevOps.Properties;

namespace REBUSS.Pure.AzureDevOps.Api
{
    /// <summary>
    /// HTTP client for Azure DevOps REST API.
    /// Expects a pre-configured HttpClient (BaseAddress + auth header) injected via IHttpClientFactory.
    /// </summary>
    public class AzureDevOpsApiClient : IAzureDevOpsApiClient
    {
        private readonly HttpClient _httpClient;
        private readonly AzureDevOpsOptions _options;
        private readonly ILogger<AzureDevOpsApiClient> _logger;

        public AzureDevOpsApiClient(
            HttpClient httpClient,
            IOptions<AzureDevOpsOptions> options,
            ILogger<AzureDevOpsApiClient> logger)
        {
            _httpClient = httpClient;
            _options = options.Value;
            _logger = logger;

            if (_httpClient.BaseAddress is null && !string.IsNullOrWhiteSpace(_options.OrganizationName))
            {
                _httpClient.BaseAddress = new Uri($"https://{Names.Domain}/{_options.OrganizationName}/");
            }
        }

        public async Task<string> GetPullRequestDetailsAsync(int pullRequestId)
        {
            _logger.LogDebug("API call: GetPullRequestDetails for PR #{PullRequestId}", pullRequestId);

            var url = $"{_options.ProjectName}/_apis/git/repositories/{_options.RepositoryName}" +
                      $"/pullRequests/{pullRequestId}?api-version=7.0";

            return await GetStringAsync(url, "GetPullRequestDetails");
        }

        public async Task<string> GetPullRequestIterationsAsync(int pullRequestId)
        {
            _logger.LogDebug("API call: GetPullRequestIterations for PR #{PullRequestId}", pullRequestId);

            var url = $"{_options.ProjectName}/_apis/git/repositories/{_options.RepositoryName}" +
                      $"/pullRequests/{pullRequestId}/iterations?api-version=7.0";

            return await GetStringAsync(url, "GetPullRequestIterations");
        }

        public async Task<string> GetPullRequestIterationChangesAsync(int pullRequestId, int iterationId)
        {
            _logger.LogDebug("API call: GetPullRequestIterationChanges for PR #{PullRequestId}, iteration {IterationId}",
                pullRequestId, iterationId);

            var url = $"{_options.ProjectName}/_apis/git/repositories/{_options.RepositoryName}" +
                      $"/pullRequests/{pullRequestId}/iterations/{iterationId}/changes?api-version=7.0";

            return await GetStringAsync(url, "GetPullRequestIterationChanges");
        }

        public async Task<string> GetPullRequestCommitsAsync(int pullRequestId)
        {
            _logger.LogDebug("API call: GetPullRequestCommits for PR #{PullRequestId}", pullRequestId);

            var url = $"{_options.ProjectName}/_apis/git/repositories/{_options.RepositoryName}" +
                      $"/pullRequests/{pullRequestId}/commits?api-version=7.0";

            return await GetStringAsync(url, "GetPullRequestCommits");
        }

        public async Task<string?> GetFileContentAtCommitAsync(string commitId, string filePath)
        {
            try
            {
                var encodedPath = Uri.EscapeDataString(filePath);
                var url = $"{_options.ProjectName}/_apis/git/repositories/{_options.RepositoryName}/items" +
                          $"?path={encodedPath}" +
                          $"&versionDescriptor.version={commitId}" +
                          $"&versionDescriptor.versionType=commit" +
                          $"&$format=text" +
                          $"&api-version=7.0";

                _logger.LogDebug("Fetching file content for {FilePath} at commit {CommitId}", filePath, commitId);

                var sw = Stopwatch.StartNew();
                var response = await _httpClient.GetAsync(url);
                sw.Stop();

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _logger.LogDebug(
                        "File {FilePath} not found at commit {CommitId} (new/deleted file) [{StatusCode}, {ElapsedMs}ms]",
                        filePath, commitId, (int)response.StatusCode, sw.ElapsedMilliseconds);
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning(
                        "Items API returned {StatusCode} for {FilePath}@{CommitId} in {ElapsedMs}ms: {Error}",
                        (int)response.StatusCode, filePath, commitId, sw.ElapsedMilliseconds, error);
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync();

                _logger.LogDebug(
                    "GetFileContentAtCommit {FilePath}@{CommitId} completed: {StatusCode}, {ResponseLength} chars, {ElapsedMs}ms",
                    filePath, commitId, (int)response.StatusCode, content.Length, sw.ElapsedMilliseconds);

                return content;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching file content for {FilePath} at {CommitId}", filePath, commitId);
                return null;
            }
        }

        public async Task<string?> GetFileContentAtRefAsync(string gitRef, string filePath)
        {
            try
            {
                var (version, versionType) = ResolveVersionDescriptor(gitRef);
                var encodedPath = Uri.EscapeDataString(filePath);
                var url = $"{_options.ProjectName}/_apis/git/repositories/{_options.RepositoryName}/items" +
                          $"?path={encodedPath}" +
                          $"&versionDescriptor.version={version}" +
                          $"&versionDescriptor.versionType={versionType}" +
                          $"&$format=text" +
                          $"&api-version=7.0";

                _logger.LogDebug("Fetching file content for {FilePath} at ref {GitRef} (type={VersionType})",
                    filePath, gitRef, versionType);

                var sw = Stopwatch.StartNew();
                var response = await _httpClient.GetAsync(url);
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
                    var error = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning(
                        "Items API returned {StatusCode} for {FilePath}@{GitRef} in {ElapsedMs}ms: {Error}",
                        (int)response.StatusCode, filePath, gitRef, sw.ElapsedMilliseconds, error);
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync();

                _logger.LogDebug(
                    "GetFileContentAtRef {FilePath}@{GitRef} completed: {StatusCode}, {ResponseLength} chars, {ElapsedMs}ms",
                    filePath, gitRef, (int)response.StatusCode, content.Length, sw.ElapsedMilliseconds);

                return content;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching file content for {FilePath} at ref {GitRef}", filePath, gitRef);
                return null;
            }
        }

        public static (string version, string versionType) ResolveVersionDescriptor(string gitRef)
        {
            if (gitRef.StartsWith("refs/tags/", StringComparison.OrdinalIgnoreCase))
                return (gitRef["refs/tags/".Length..], "tag");

            if (gitRef.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase))
                return (gitRef["refs/heads/".Length..], "branch");

            if (gitRef.Length >= 7 && gitRef.All(c => char.IsAsciiHexDigit(c)))
                return (gitRef, "commit");

            return (gitRef, "branch");
        }

        /// <summary>
        /// Sends a GET request to the specified relative URL and returns the response body as a string.
        /// Logs the full URL at Debug level and throws on non-success status codes.
        /// Also detects non-JSON (HTML) responses that indicate authentication failures
        /// (e.g. HTTP 203 returning a login page instead of API data).
        /// </summary>
        private async Task<string> GetStringAsync(string relativeUrl, string endpointName = "Unknown")
        {
            var fullUrl = new Uri(_httpClient.BaseAddress!, relativeUrl);
            _logger.LogDebug("GET {FullUrl}", fullUrl);

            var sw = Stopwatch.StartNew();
            var response = await _httpClient.GetAsync(relativeUrl);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                var truncated = errorContent.Length > 500
                    ? errorContent[..500] + "...[truncated]"
                    : errorContent;
                _logger.LogError(
                    "Azure DevOps API {Endpoint} returned {StatusCode} in {ElapsedMs}ms: {ErrorContent}",
                    endpointName, (int)response.StatusCode, sw.ElapsedMilliseconds, truncated);
            }

            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync();

            // Detect HTML responses on 2xx status codes (e.g. 203 Non-Authoritative).
            // Azure DevOps returns login/redirect HTML pages when authentication fails
            // but the CDN/proxy returns a 2xx status code instead of 401.
            if (IsHtmlResponse(response, body))
            {
                _logger.LogError(
                    "Azure DevOps API {Endpoint} returned HTML instead of JSON (status {StatusCode}) — " +
                    "this usually means the authentication token is invalid or expired. " +
                    "Run 'rebuss-pure init' or 'az login' to re-authenticate.",
                    endpointName, (int)response.StatusCode);

                throw new HttpRequestException(
                    string.Format(Resources.ErrorAuthPageInsteadOfApiData, (int)response.StatusCode),
                    inner: null,
                    statusCode: System.Net.HttpStatusCode.Unauthorized);
            }

            _logger.LogDebug(
                "Azure DevOps API {Endpoint} completed: {StatusCode}, {ResponseLength} chars, {ElapsedMs}ms",
                endpointName, (int)response.StatusCode, body.Length, sw.ElapsedMilliseconds);

            return body;
        }

        /// <summary>
        /// Detects whether a 2xx response actually contains HTML instead of JSON.
        /// This happens when Azure DevOps returns an authentication/login page
        /// through a CDN or proxy that doesn't convert it to a proper 401.
        /// </summary>
        internal static bool IsHtmlResponse(HttpResponseMessage response, string body)
        {
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (string.Equals(contentType, Resources.ContentTypeHtml, StringComparison.OrdinalIgnoreCase))
                return true;

            // Fallback: check body content for HTML markers when content-type is missing or ambiguous
            var trimmed = body.TrimStart();
            if (trimmed.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }
    }
}
