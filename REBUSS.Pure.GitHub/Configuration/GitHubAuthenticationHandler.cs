using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace REBUSS.Pure.GitHub.Configuration;

/// <summary>
/// A <see cref="DelegatingHandler"/> that adds the GitHub Personal Access Token
/// as a Bearer token on each outgoing request, along with required GitHub API headers.
/// </summary>
public class GitHubAuthenticationHandler : DelegatingHandler
{
    private readonly IOptions<GitHubOptions> _options;
    private readonly ILogger<GitHubAuthenticationHandler> _logger;

    public GitHubAuthenticationHandler(
        IOptions<GitHubOptions> options,
        ILogger<GitHubAuthenticationHandler> logger)
    {
        _options = options;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var pat = _options.Value.PersonalAccessToken;
        if (!string.IsNullOrWhiteSpace(pat))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pat);
        }
        else
        {
            _logger.LogWarning("No GitHub Personal Access Token configured — API calls may fail or be rate-limited");
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        if (request.Headers.UserAgent.Count == 0)
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("REBUSS-Pure", "1.0"));

        return await base.SendAsync(request, cancellationToken);
    }
}
