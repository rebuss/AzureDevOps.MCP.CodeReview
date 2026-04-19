using REBUSS.Pure.GitHub.Api;
using REBUSS.Pure.Core;

namespace REBUSS.Pure.GitHub.Providers;

/// <summary>
/// GitHub implementation of <see cref="IRepositoryArchiveProvider"/>.
/// Delegates to the API client to download the repository ZIP.
/// </summary>
public class GitHubRepositoryArchiveProvider : IRepositoryArchiveProvider
{
    private readonly IGitHubApiClient _apiClient;

    public GitHubRepositoryArchiveProvider(IGitHubApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    public Task DownloadRepositoryZipAsync(string commitRef, string destinationPath, CancellationToken ct = default)
        => _apiClient.DownloadRepositoryZipToFileAsync(commitRef, destinationPath, ct);
}
