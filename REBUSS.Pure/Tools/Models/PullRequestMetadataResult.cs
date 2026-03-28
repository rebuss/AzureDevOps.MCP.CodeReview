using System.Text.Json.Serialization;

namespace REBUSS.Pure.Tools.Models
{
    /// <summary>
    /// Structured JSON response model for the get_pr_metadata tool.
    /// </summary>
    public class PullRequestMetadataResult
    {
        [JsonPropertyName("prNumber")]
        public int PrNumber { get; set; }

        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("author")]
        public AuthorInfo Author { get; set; } = new();

        [JsonPropertyName("state")]
        public string State { get; set; } = string.Empty;

        [JsonPropertyName("isDraft")]
        public bool IsDraft { get; set; }

        [JsonPropertyName("createdAt")]
        public string CreatedAt { get; set; } = string.Empty;

        [JsonPropertyName("updatedAt")]
        public string? UpdatedAt { get; set; }

        [JsonPropertyName("base")]
        public RefInfo Base { get; set; } = new();

        [JsonPropertyName("head")]
        public RefInfo Head { get; set; } = new();

        [JsonPropertyName("stats")]
        public PrStats Stats { get; set; } = new();

        [JsonPropertyName("commitShas")]
        public List<string> CommitShas { get; set; } = new();

        [JsonPropertyName("description")]
        public DescriptionInfo Description { get; set; } = new();

        [JsonPropertyName("source")]
        public SourceInfo Source { get; set; } = new();

        [JsonPropertyName("contentPaging")]
        public ContentPagingInfo? ContentPaging { get; set; }
    }

    public class AuthorInfo
    {
        [JsonPropertyName("login")]
        public string Login { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = string.Empty;
    }

    public class RefInfo
    {
        [JsonPropertyName("ref")]
        public string Ref { get; set; } = string.Empty;

        [JsonPropertyName("sha")]
        public string Sha { get; set; } = string.Empty;
    }

    public class PrStats
    {
        [JsonPropertyName("commits")]
        public int Commits { get; set; }

        [JsonPropertyName("changedFiles")]
        public int ChangedFiles { get; set; }

        [JsonPropertyName("additions")]
        public int Additions { get; set; }

        [JsonPropertyName("deletions")]
        public int Deletions { get; set; }
    }

    public class DescriptionInfo
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("isTruncated")]
        public bool IsTruncated { get; set; }

        [JsonPropertyName("originalLength")]
        public int OriginalLength { get; set; }

        [JsonPropertyName("returnedLength")]
        public int ReturnedLength { get; set; }
    }

    public class SourceInfo
    {
        [JsonPropertyName("repository")]
        public string Repository { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;
    }
}
