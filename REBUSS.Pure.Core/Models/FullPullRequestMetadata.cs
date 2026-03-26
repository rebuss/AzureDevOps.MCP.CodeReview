namespace REBUSS.Pure.Core.Models
{
    /// <summary>
    /// Rich metadata model for a pull request, populated from multiple API endpoints.
    /// </summary>
    public sealed class FullPullRequestMetadata
    {
        public int PullRequestId { get; set; }
        public int CodeReviewId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public bool IsDraft { get; set; }
        public string AuthorLogin { get; set; } = string.Empty;
        public string AuthorDisplayName { get; set; } = string.Empty;
        public DateTime CreatedDate { get; set; }
        public DateTime? ClosedDate { get; set; }
        public string SourceBranch { get; set; } = string.Empty;
        public string TargetBranch { get; set; } = string.Empty;
        public string SourceRefName { get; set; } = string.Empty;
        public string TargetRefName { get; set; } = string.Empty;
        public string LastMergeSourceCommitId { get; set; } = string.Empty;
        public string LastMergeTargetCommitId { get; set; } = string.Empty;
        public string RepositoryName { get; set; } = string.Empty;
        public string ProjectName { get; set; } = string.Empty;
        public List<string> CommitShas { get; set; } = new();
        public int ChangedFilesCount { get; set; }
        public int Additions { get; set; }
        public int Deletions { get; set; }

        /// <summary>
        /// Full repository identifier as displayed by the provider (e.g. "org/project/repo" for Azure DevOps,
        /// "owner/repo" for GitHub). Populated by the provider implementation.
        /// </summary>
        public string RepositoryFullName { get; set; } = string.Empty;

        /// <summary>
        /// Web URL to view this pull request in the provider's UI. Populated by the provider implementation.
        /// </summary>
        public string WebUrl { get; set; } = string.Empty;
    }
}
