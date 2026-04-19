using NSubstitute;
using NSubstitute.ExceptionExtensions;
using REBUSS.Pure.GitHub.Api;
using REBUSS.Pure.GitHub.Providers;

namespace REBUSS.Pure.GitHub.Tests.Providers;

public class GitHubRepositoryArchiveProviderTests
{
    private readonly IGitHubApiClient _apiClient = Substitute.For<IGitHubApiClient>();
    private readonly GitHubRepositoryArchiveProvider _provider;

    public GitHubRepositoryArchiveProviderTests()
    {
        _provider = new GitHubRepositoryArchiveProvider(_apiClient);
    }

    [Fact]
    public async Task DownloadRepositoryZipAsync_Success_CallsApiClient()
    {
        _apiClient.DownloadRepositoryZipToFileAsync("abc123", "/tmp/test.zip", Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _provider.DownloadRepositoryZipAsync("abc123", "/tmp/test.zip");

        await _apiClient.Received(1).DownloadRepositoryZipToFileAsync("abc123", "/tmp/test.zip", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DownloadRepositoryZipAsync_ApiError_Throws()
    {
        _apiClient.DownloadRepositoryZipToFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Server error"));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => _provider.DownloadRepositoryZipAsync("abc123", "/tmp/test.zip"));
    }
}
