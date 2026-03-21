using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.Services.LocalReview
{
    /// <summary>
    /// Provides structured review data derived from a local git repository.
    /// </summary>
    public interface ILocalReviewProvider
    {
        /// <summary>
        /// Returns the list of changed files for the given <paramref name="scope"/>,
        /// classified by type and review priority.
        /// </summary>
        /// <exception cref="LocalRepositoryNotFoundException">
        /// Thrown when no valid git repository root can be resolved.
        /// </exception>
        Task<LocalReviewFiles> GetFilesAsync(
            LocalReviewScope scope,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns a structured diff for a single file within the given <paramref name="scope"/>.
        /// </summary>
        /// <exception cref="LocalRepositoryNotFoundException">
        /// Thrown when no valid git repository root can be resolved.
        /// </exception>
        /// <exception cref="LocalFileNotFoundException">
        /// Thrown when the requested file is not among the changed files.
        /// </exception>
        Task<PullRequestDiff> GetFileDiffAsync(
            string filePath,
            LocalReviewScope scope,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Result of listing locally changed files, analogous to <see cref="PullRequestFiles"/>.
    /// </summary>
    public sealed class LocalReviewFiles
    {
        public string RepositoryRoot { get; set; } = string.Empty;
        public string Scope { get; set; } = string.Empty;
        public string? CurrentBranch { get; set; }
        public List<PullRequestFileInfo> Files { get; set; } = new();
        public PullRequestFilesSummary Summary { get; set; } = new();
    }
}
