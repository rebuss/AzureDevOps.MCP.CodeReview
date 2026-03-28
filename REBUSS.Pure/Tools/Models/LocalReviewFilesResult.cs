using System.Text.Json.Serialization;

namespace REBUSS.Pure.Tools.Models
{
    /// <summary>
    /// Structured JSON response model for the <c>get_local_files</c> tool.
    /// Mirrors <see cref="PullRequestFilesResult"/> but includes local-review context fields.
    /// </summary>
    public class LocalReviewFilesResult
    {
        [JsonPropertyName("repositoryRoot")]
        public string RepositoryRoot { get; set; } = string.Empty;

        [JsonPropertyName("scope")]
        public string Scope { get; set; } = string.Empty;

        [JsonPropertyName("currentBranch")]
        public string? CurrentBranch { get; set; }

        [JsonPropertyName("totalFiles")]
        public int TotalFiles { get; set; }

        [JsonPropertyName("files")]
        public List<PullRequestFileItem> Files { get; set; } = new();

        [JsonPropertyName("summary")]
        public PullRequestFilesSummaryResult Summary { get; set; } = new();

        [JsonPropertyName("manifest")]
        public ContentManifestResult? Manifest { get; set; }

        /// <summary>Present when paginated (explicit budget provided). Feature 004.</summary>
        [JsonPropertyName("pagination")]
        public PaginationMetadataResult? Pagination { get; set; }
    }
}
