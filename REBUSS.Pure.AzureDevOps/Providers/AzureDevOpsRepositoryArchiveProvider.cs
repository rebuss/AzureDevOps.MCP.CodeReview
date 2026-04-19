using REBUSS.Pure.AzureDevOps.Api;
using REBUSS.Pure.Core;

namespace REBUSS.Pure.AzureDevOps.Providers;

/// <summary>
/// Azure DevOps implementation of <see cref="IRepositoryArchiveProvider"/>.
/// Delegates to the API client to download the repository ZIP.
/// </summary>
public class AzureDevOpsRepositoryArchiveProvider : IRepositoryArchiveProvider
{
    private readonly IAzureDevOpsApiClient _apiClient;

    public AzureDevOpsRepositoryArchiveProvider(IAzureDevOpsApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    public Task DownloadRepositoryZipAsync(string commitRef, string destinationPath, CancellationToken ct = default)
        => _apiClient.DownloadRepositoryZipToFileAsync(commitRef, destinationPath, ct);
}
