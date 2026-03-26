namespace REBUSS.Pure.AzureDevOps.Api
{
    /// <summary>
    /// Interface for Azure DevOps REST API client.
    /// All methods return raw JSON strings from the Azure DevOps REST API.
    /// </summary>
    public interface IAzureDevOpsApiClient
    {
        /// <summary>
        /// Gets pull request details (title, status, branches, etc.).
        /// </summary>
        Task<string> GetPullRequestDetailsAsync(int pullRequestId);

        /// <summary>
        /// Gets all iterations for a pull request.
        /// </summary>
        Task<string> GetPullRequestIterationsAsync(int pullRequestId);

        /// <summary>
        /// Gets file changes for a specific iteration.
        /// </summary>
        Task<string> GetPullRequestIterationChangesAsync(int pullRequestId, int iterationId);

        /// <summary>
        /// Gets all commits associated with a pull request.
        /// </summary>
        Task<string> GetPullRequestCommitsAsync(int pullRequestId);

        /// <summary>
        /// Fetches the raw text content of a single file at a specific commit SHA.
        /// Returns null when the file does not exist at that commit (e.g. newly added or deleted file).
        /// </summary>
        Task<string?> GetFileContentAtCommitAsync(string commitId, string filePath);

        /// <summary>
        /// Fetches the raw text content of a single file at a specific Git ref (commit SHA, branch, or tag).
        /// Returns null when the file does not exist at that ref.
        /// </summary>
        Task<string?> GetFileContentAtRefAsync(string gitRef, string filePath);
    }
}
