using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using REBUSS.Pure.GitHub.Api;
using REBUSS.Pure.GitHub.Configuration;

namespace REBUSS.Pure.GitHub.Tests.Api;

/// <summary>
/// Tests for the static PR details cache in <see cref="GitHubApiClient"/>.
/// Uses unique PR numbers per test to avoid cross-test interference from the static cache.
/// </summary>
public class GitHubApiClientCacheTests
{
    private static int _nextPrNumber = 10000; // High base to avoid collisions with other tests

    private static int UniquePrNumber() => Interlocked.Increment(ref _nextPrNumber);

    private static GitHubApiClient CreateClient(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.github.com/")
        };

        var options = Options.Create(new GitHubOptions
        {
            Owner = "test-owner",
            RepositoryName = "test-repo"
        });

        return new GitHubApiClient(httpClient, options, NullLogger<GitHubApiClient>.Instance);
    }

    [Fact]
    public async Task GetPullRequestDetailsAsync_SecondCall_ReturnsCached_NoHttpRequest()
    {
        var prNumber = UniquePrNumber();
        var callCount = 0;
        var handler = new FakeHandler(_ =>
        {
            Interlocked.Increment(ref callCount);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"number\":{prNumber},\"title\":\"test\"}}")
            };
        });

        var client = CreateClient(handler);

        var first = await client.GetPullRequestDetailsAsync(prNumber);
        var second = await client.GetPullRequestDetailsAsync(prNumber);

        Assert.Equal(first, second);
        Assert.Equal(1, callCount); // HTTP called only once
    }

    [Fact]
    public async Task GetPullRequestDetailsAsync_DifferentPrNumbers_FetchSeparately()
    {
        var pr1 = UniquePrNumber();
        var pr2 = UniquePrNumber();
        var callCount = 0;
        var handler = new FakeHandler(req =>
        {
            Interlocked.Increment(ref callCount);
            var num = req.RequestUri!.Segments[^1];
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"number\":{num},\"title\":\"pr-{num}\"}}")
            };
        });

        var client = CreateClient(handler);

        var result1 = await client.GetPullRequestDetailsAsync(pr1);
        var result2 = await client.GetPullRequestDetailsAsync(pr2);

        Assert.NotEqual(result1, result2);
        Assert.Contains($"{pr1}", result1);
        Assert.Contains($"{pr2}", result2);
        Assert.Equal(2, callCount); // Each PR fetched once
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _factory;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> factory) => _factory = factory;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_factory(request));
    }
}
