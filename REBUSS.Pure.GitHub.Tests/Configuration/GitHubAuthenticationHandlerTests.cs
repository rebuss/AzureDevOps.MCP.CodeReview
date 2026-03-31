using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using REBUSS.Pure.GitHub.Configuration;

namespace REBUSS.Pure.GitHub.Tests.Configuration;

public class GitHubAuthenticationHandlerTests
{
    private readonly IGitHubAuthenticationProvider _authProvider = Substitute.For<IGitHubAuthenticationProvider>();

    public GitHubAuthenticationHandlerTests()
    {
        _authProvider.GetAuthenticationAsync(Arg.Any<CancellationToken>())
            .Returns(new AuthenticationHeaderValue("Bearer", "test-token"));
    }

    private GitHubAuthenticationHandler CreateHandler(HttpResponseMessage innerResponse)
    {
        var handler = new GitHubAuthenticationHandler(
            _authProvider,
            NullLogger<GitHubAuthenticationHandler>.Instance)
        {
            InnerHandler = new StubHandler(innerResponse)
        };

        return handler;
    }

    [Fact]
    public async Task SendAsync_DoesNotRetry_WhenPrimaryRateLimitExceeded()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.Add("X-RateLimit-Remaining", "0");

        var handler = CreateHandler(response);
        using var client = new HttpClient(handler);

        var result = await client.GetAsync("https://api.github.com/test");

        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        _authProvider.DidNotReceive().InvalidateCachedToken();
    }

    [Fact]
    public async Task SendAsync_DoesNotRetry_WhenSecondaryRateLimitExceeded()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.Add("Retry-After", "60");

        var handler = CreateHandler(response);
        using var client = new HttpClient(handler);

        var result = await client.GetAsync("https://api.github.com/test");

        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        _authProvider.DidNotReceive().InvalidateCachedToken();
    }

    [Fact]
    public async Task SendAsync_Retries_WhenForbiddenWithoutRateLimitHeaders()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden);

        var handler = CreateHandler(response);
        using var client = new HttpClient(handler);

        var result = await client.GetAsync("https://api.github.com/test");

        _authProvider.Received(1).InvalidateCachedToken();
    }

    [Fact]
    public async Task SendAsync_Retries_WhenUnauthorized()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var handler = CreateHandler(response);
        using var client = new HttpClient(handler);

        var result = await client.GetAsync("https://api.github.com/test");

        _authProvider.Received(1).InvalidateCachedToken();
    }

    [Fact]
    public async Task SendAsync_DoesNotRetry_WhenSuccess()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);

        var handler = CreateHandler(response);
        using var client = new HttpClient(handler);

        var result = await client.GetAsync("https://api.github.com/test");

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        _authProvider.DidNotReceive().InvalidateCachedToken();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public StubHandler(HttpResponseMessage response) => _response = response;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_response);
    }
}
