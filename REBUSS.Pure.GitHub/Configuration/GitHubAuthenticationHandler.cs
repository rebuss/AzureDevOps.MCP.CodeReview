using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.GitHub.Properties;

namespace REBUSS.Pure.GitHub.Configuration;

/// <summary>
/// A <see cref="DelegatingHandler"/> that lazily resolves the authentication header
/// on each outgoing request via <see cref="IGitHubAuthenticationProvider"/>,
/// along with required GitHub API headers.
/// <para>
/// When the server returns HTTP 401 or 403, the handler invalidates the cached token,
/// acquires a fresh one via the authentication provider, and retries the request once.
/// </para>
/// </summary>
public class GitHubAuthenticationHandler : DelegatingHandler
{
    private readonly IGitHubAuthenticationProvider _authenticationProvider;
    private readonly ILogger<GitHubAuthenticationHandler> _logger;

    public GitHubAuthenticationHandler(
        IGitHubAuthenticationProvider authenticationProvider,
        ILogger<GitHubAuthenticationHandler> logger)
    {
        _authenticationProvider = authenticationProvider;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.Authorization = await _authenticationProvider.GetAuthenticationAsync(cancellationToken);
        SetGitHubHeaders(request);

        var response = await base.SendAsync(request, cancellationToken);

        if (IsAuthFailureResponse(response))
        {
            _logger.LogWarning(
                "Received HTTP {StatusCode} — token appears invalid. Invalidating cache and retrying with a fresh token.",
                (int)response.StatusCode);

            _authenticationProvider.InvalidateCachedToken();

            var retryRequest = await CloneRequestAsync(request, cancellationToken);
            retryRequest.Headers.Authorization = await _authenticationProvider.GetAuthenticationAsync(cancellationToken);
            SetGitHubHeaders(retryRequest);

            response.Dispose();
            response = await base.SendAsync(retryRequest, cancellationToken);
        }

        return response;
    }

    private static void SetGitHubHeaders(HttpRequestMessage request)
    {
        if (!request.Headers.Accept.Contains(new MediaTypeWithQualityHeaderValue(Resources.GitHubAcceptHeader)))
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(Resources.GitHubAcceptHeader));

        if (!request.Headers.Contains(Resources.GitHubApiVersionHeader))
            request.Headers.Add(Resources.GitHubApiVersionHeader, Resources.GitHubApiVersion);

        if (request.Headers.UserAgent.Count == 0)
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue(Resources.HttpUserAgentProduct, "1.0"));
    }

    /// <summary>
    /// Detects an authentication failure (HTTP 401 Unauthorized or 403 Forbidden).
    /// GitHub returns 403 with <c>X-RateLimit-Remaining: 0</c> for primary rate limits,
    /// and 403 with <c>Retry-After</c> header for secondary rate limits —
    /// retrying with a fresh token would not help in either case.
    /// </summary>
    private static bool IsAuthFailureResponse(HttpResponseMessage response)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            return true;

        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            // Primary rate limit: X-RateLimit-Remaining: 0
            if (response.Headers.TryGetValues(Resources.GitHubRateLimitRemainingHeader, out var values) &&
                values.FirstOrDefault() == "0")
                return false;

            // Secondary rate limit: Retry-After header present
            if (response.Headers.Contains(Resources.GitHubRetryAfterHeader))
                return false;

            return true;
        }

        return false;
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(
        HttpRequestMessage original,
        CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri);

        if (original.Content is not null)
        {
            var content = await original.Content.ReadAsByteArrayAsync(cancellationToken);
            clone.Content = new ByteArrayContent(content);

            foreach (var header in original.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var header in original.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        return clone;
    }
}
