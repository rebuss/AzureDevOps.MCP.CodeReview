using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using REBUSS.Pure.AzureDevOps.Configuration;
using REBUSS.Pure.AzureDevOps.Api;

namespace REBUSS.Pure.Tests.AzureDevOpsIntegration;

public class AzureDevOpsApiClientTests
{
    private readonly AzureDevOpsOptions _options = new()
    {
        OrganizationName = "TestOrg",
        ProjectName = "TestProject",
        RepositoryName = "TestRepo",
        PersonalAccessToken = "fake-pat"
    };

    private AzureDevOpsApiClient CreateClient(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://dev.azure.com/{_options.OrganizationName}/")
        };

        return new AzureDevOpsApiClient(
            httpClient,
            Options.Create(_options),
            NullLogger<AzureDevOpsApiClient>.Instance);
    }

    // ---- GetPullRequestDetailsAsync ----

    [Fact]
    public async Task GetPullRequestDetailsAsync_ReturnsJsonBody()
    {
        var expected = """{"title":"Fix bug","status":"active"}""";
        var handler = new FakeHandler(HttpStatusCode.OK, expected);
        var client = CreateClient(handler);

        var result = await client.GetPullRequestDetailsAsync(42);

        Assert.Equal(expected, result);
        Assert.Contains("/pullRequests/42?api-version=7.0", handler.LastRequestUri!.ToString());
    }

    [Fact]
    public async Task GetPullRequestDetailsAsync_ThrowsOnNonSuccess()
    {
        var handler = new FakeHandler(HttpStatusCode.NotFound, "Not Found");
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetPullRequestDetailsAsync(999));
    }

    // ---- GetPullRequestIterationsAsync ----

    [Fact]
    public async Task GetPullRequestIterationsAsync_ReturnsJsonBody()
    {
        var expected = """{"value":[{"id":1},{"id":2}]}""";
        var handler = new FakeHandler(HttpStatusCode.OK, expected);
        var client = CreateClient(handler);

        var result = await client.GetPullRequestIterationsAsync(10);

        Assert.Equal(expected, result);
        Assert.Contains("/pullRequests/10/iterations?api-version=7.0", handler.LastRequestUri!.ToString());
    }

    // ---- GetPullRequestIterationChangesAsync ----

    [Fact]
    public async Task GetPullRequestIterationChangesAsync_BuildsCorrectUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var client = CreateClient(handler);

        await client.GetPullRequestIterationChangesAsync(10, 2);

        Assert.Contains("/pullRequests/10/iterations/2/changes?api-version=7.0",
            handler.LastRequestUri!.ToString());
    }

    // ---- GetFileContentAtCommitAsync ----

    [Fact]
    public async Task GetFileContentAtCommitAsync_ReturnsContent()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "file content here");
        var client = CreateClient(handler);

        var result = await client.GetFileContentAtCommitAsync("abc123", "/src/File.cs");

        Assert.Equal("file content here", result);
    }

    [Fact]
    public async Task GetFileContentAtCommitAsync_ReturnsNull_WhenNotFound()
    {
        var handler = new FakeHandler(HttpStatusCode.NotFound, "");
        var client = CreateClient(handler);

        var result = await client.GetFileContentAtCommitAsync("abc123", "/src/File.cs");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetFileContentAtCommitAsync_ReturnsNull_OnServerError()
    {
        var handler = new FakeHandler(HttpStatusCode.InternalServerError, "boom");
        var client = CreateClient(handler);

        var result = await client.GetFileContentAtCommitAsync("abc123", "/src/File.cs");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetFileContentAtCommitAsync_EncodesFilePath()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "ok");
        var client = CreateClient(handler);

        await client.GetFileContentAtCommitAsync("abc", "/src/My File.cs");

        // Uri.EscapeDataString encodes the path; verify via OriginalString
        var url = handler.LastRequestUri!.OriginalString;
        Assert.DoesNotContain("My File.cs", url);
        Assert.Contains("versionDescriptor.version=abc", url);
    }

    // ---- GetFileContentAtRefAsync ----

    [Fact]
    public async Task GetFileContentAtRefAsync_ReturnsContent()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "file content at ref");
        var client = CreateClient(handler);

        var result = await client.GetFileContentAtRefAsync("abc123def456abc123def456abc123def456abc1", "/src/File.cs");

        Assert.Equal("file content at ref", result);
        var url = handler.LastRequestUri!.OriginalString;
        Assert.Contains("versionDescriptor.versionType=commit", url);
    }

    [Fact]
    public async Task GetFileContentAtRefAsync_ReturnsNull_WhenNotFound()
    {
        var handler = new FakeHandler(HttpStatusCode.NotFound, "");
        var client = CreateClient(handler);

        var result = await client.GetFileContentAtRefAsync("abc123", "/src/File.cs");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetFileContentAtRefAsync_UsesBranchType_ForBranchName()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "branch content");
        var client = CreateClient(handler);

        var result = await client.GetFileContentAtRefAsync("feature/cache-fix", "/src/File.cs");

        Assert.Equal("branch content", result);
        var url = handler.LastRequestUri!.OriginalString;
        Assert.Contains("versionDescriptor.versionType=branch", url);
        Assert.Contains("versionDescriptor.version=feature/cache-fix", url);
    }

    [Fact]
    public async Task GetFileContentAtRefAsync_UsesTagType_ForTagRef()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "tag content");
        var client = CreateClient(handler);

        var result = await client.GetFileContentAtRefAsync("refs/tags/v1.0", "/src/File.cs");

        Assert.Equal("tag content", result);
        var url = handler.LastRequestUri!.OriginalString;
        Assert.Contains("versionDescriptor.versionType=tag", url);
        Assert.Contains("versionDescriptor.version=v1.0", url);
    }

    [Fact]
    public async Task GetFileContentAtRefAsync_UsesBranchType_ForRefsHeadsPrefix()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "branch content");
        var client = CreateClient(handler);

        var result = await client.GetFileContentAtRefAsync("refs/heads/main", "/src/File.cs");

        Assert.Equal("branch content", result);
        var url = handler.LastRequestUri!.OriginalString;
        Assert.Contains("versionDescriptor.versionType=branch", url);
        Assert.Contains("versionDescriptor.version=main", url);
    }

    // ---- ResolveVersionDescriptor ----

    [Theory]
    [InlineData("abc123def456abc123def456abc123def456abc1", "abc123def456abc123def456abc123def456abc1", "commit")]
    [InlineData("abcdef1", "abcdef1", "commit")]
    [InlineData("refs/tags/v1.0", "v1.0", "tag")]
    [InlineData("refs/heads/main", "main", "branch")]
    [InlineData("refs/heads/feature/x", "feature/x", "branch")]
    [InlineData("main", "main", "branch")]
    [InlineData("feature/cache-fix", "feature/cache-fix", "branch")]
    public void ResolveVersionDescriptor_ResolvesCorrectly(string input, string expectedVersion, string expectedType)
    {
        var (version, versionType) = AzureDevOpsApiClient.ResolveVersionDescriptor(input);

        Assert.Equal(expectedVersion, version);
        Assert.Equal(expectedType, versionType);
    }

    // ---- URL construction includes project & repo ----

    [Fact]
    public async Task AllMethods_IncludeProjectAndRepoInUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var client = CreateClient(handler);

        await client.GetPullRequestDetailsAsync(1);
        var detailsUrl = handler.LastRequestUri!.ToString();

        await client.GetPullRequestIterationsAsync(1);
        var iterationsUrl = handler.LastRequestUri!.ToString();

        await client.GetPullRequestIterationChangesAsync(1, 1);
        var changesUrl = handler.LastRequestUri!.ToString();

        foreach (var url in new[] { detailsUrl, iterationsUrl, changesUrl })
        {
            Assert.Contains("TestProject", url);
            Assert.Contains("TestRepo", url);
        }
    }

    // ---- HTML response detection ----

    [Fact]
    public async Task GetPullRequestDetailsAsync_ThrowsOnHtmlResponse_WithStatusCode203()
    {
        var htmlContent = "<!DOCTYPE html><html><head><title>Login</title></head><body></body></html>";
        var handler = new FakeHandler((HttpStatusCode)203, htmlContent, "text/html");
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetPullRequestDetailsAsync(42));

        Assert.Contains("authentication", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    [Fact]
    public async Task GetPullRequestDetailsAsync_ThrowsOnHtmlBody_EvenWithoutHtmlContentType()
    {
        var htmlContent = "<!DOCTYPE html><html><body>Unable to complete authentication</body></html>";
        var handler = new FakeHandler(HttpStatusCode.OK, htmlContent);
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetPullRequestDetailsAsync(42));

        Assert.Contains("authentication", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetPullRequestDetailsAsync_DoesNotThrow_ForValidJsonResponse()
    {
        var jsonContent = "{\"title\":\"Fix bug\"}";
        var handler = new FakeHandler(HttpStatusCode.OK, jsonContent, "application/json");
        var client = CreateClient(handler);

        var result = await client.GetPullRequestDetailsAsync(42);

        Assert.Equal(jsonContent, result);
    }

    // ---- IsHtmlResponse ----

    [Fact]
    public void IsHtmlResponse_ReturnsTrue_ForHtmlContentType()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>", System.Text.Encoding.UTF8, "text/html")
        };

        Assert.True(AzureDevOpsApiClient.IsHtmlResponse(response, "<html>"));
    }

    [Fact]
    public void IsHtmlResponse_ReturnsTrue_ForBodyStartingWithHtmlTag()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<!DOCTYPE html>", System.Text.Encoding.UTF8, "application/octet-stream")
        };

        Assert.True(AzureDevOpsApiClient.IsHtmlResponse(response, "<!DOCTYPE html>"));
    }

    [Fact]
    public void IsHtmlResponse_ReturnsFalse_ForJsonBody()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"key\":\"value\"}", System.Text.Encoding.UTF8, "application/json")
        };

        Assert.False(AzureDevOpsApiClient.IsHtmlResponse(response, "{\"key\":\"value\"}"));
    }

    [Fact]
    public void IsHtmlResponse_ReturnsFalse_ForXmlBody()
    {
        var xmlContent = "<?xml version=\"1.0\" encoding=\"utf-8\"?><error><message>Not found</message></error>";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(xmlContent, System.Text.Encoding.UTF8, "application/xml")
        };

        Assert.False(AzureDevOpsApiClient.IsHtmlResponse(response, xmlContent));
    }

    /// <summary>
    /// Minimal HttpMessageHandler stub that returns a fixed response.
    /// </summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _content;
        private readonly string? _contentType;

        public Uri? LastRequestUri { get; private set; }

        public FakeHandler(HttpStatusCode statusCode, string content, string? contentType = null)
        {
            _statusCode = statusCode;
            _content = content;
            _contentType = contentType;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            var response = new HttpResponseMessage(_statusCode);

            if (_contentType is not null)
                response.Content = new StringContent(_content, System.Text.Encoding.UTF8, _contentType);
            else
                response.Content = new StringContent(_content);

            return Task.FromResult(response);
        }
    }
}
