using NSubstitute;
using NSubstitute.ExceptionExtensions;
using REBUSS.Pure.AzureDevOps.Api;
using REBUSS.Pure.AzureDevOps.Providers;

namespace REBUSS.Pure.AzureDevOps.Tests.Providers;

public class AzureDevOpsRepositoryArchiveProviderTests
{
    private readonly IAzureDevOpsApiClient _apiClient = Substitute.For<IAzureDevOpsApiClient>();
    private readonly AzureDevOpsRepositoryArchiveProvider _provider;

    public AzureDevOpsRepositoryArchiveProviderTests()
    {
        _provider = new AzureDevOpsRepositoryArchiveProvider(_apiClient);
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
