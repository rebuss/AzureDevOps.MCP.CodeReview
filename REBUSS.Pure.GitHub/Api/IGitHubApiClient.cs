namespace REBUSS.Pure.GitHub.Api;

/// <summary>
/// Interface for GitHub REST API client.
/// All methods return raw JSON strings from the GitHub REST API v3.
/// </summary>
public interface IGitHubApiClient
{
    /// <summary>
    /// Gets pull request details (title, state, branches, head/base SHAs, etc.).
    /// </summary>
    Task<string> GetPullRequestDetailsAsync(int pullRequestNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all files changed in a pull request (paginated, returns combined results).
    /// </summary>
    Task<string> GetPullRequestFilesAsync(int pullRequestNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all commits associated with a pull request (paginated, returns combined results).
    /// </summary>
    Task<string> GetPullRequestCommitsAsync(int pullRequestNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the raw text content of a single file at a specific Git ref (commit SHA, branch, or tag).
    /// Returns null when the file does not exist at that ref.
    /// </summary>
    Task<string?> GetFileContentAtRefAsync(string gitRef, string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads the entire repository as a ZIP archive at a specific ref
    /// and writes it to <paramref name="destinationPath"/>.
    /// </summary>
    Task DownloadRepositoryZipToFileAsync(string commitRef, string destinationPath, CancellationToken cancellationToken = default);
}
