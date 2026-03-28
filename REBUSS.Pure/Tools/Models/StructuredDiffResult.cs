using System.Text.Json.Serialization;

namespace REBUSS.Pure.Tools.Models
{
    /// <summary>
    /// Structured JSON response model for diff tools.
    /// Contains hunk-level diff data optimized for AI code review.
    /// </summary>
    public class StructuredDiffResult
    {
        [JsonPropertyName("prNumber")]
        public int? PrNumber { get; set; }

        [JsonPropertyName("files")]
        public List<StructuredFileChange> Files { get; set; } = new();

        [JsonPropertyName("manifest")]
        public ContentManifestResult? Manifest { get; set; }

        /// <summary>Present when paginated (explicit budget provided). Feature 004.</summary>
        [JsonPropertyName("pagination")]
        public PaginationMetadataResult? Pagination { get; set; }

        /// <summary>Present when PR data changed since pagination started. Feature 004.</summary>
        [JsonPropertyName("stalenessWarning")]
        public StalenessWarningResult? StalenessWarning { get; set; }
    }

    public class StructuredFileChange
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("changeType")]
        public string ChangeType { get; set; } = string.Empty;

        [JsonPropertyName("skipReason")]
        public string? SkipReason { get; set; }

        [JsonPropertyName("additions")]
        public int Additions { get; set; }

        [JsonPropertyName("deletions")]
        public int Deletions { get; set; }

        [JsonPropertyName("hunks")]
        public List<StructuredHunk> Hunks { get; set; } = new();
    }

    public class StructuredHunk
    {
        [JsonPropertyName("oldStart")]
        public int OldStart { get; set; }

        [JsonPropertyName("oldCount")]
        public int OldCount { get; set; }

        [JsonPropertyName("newStart")]
        public int NewStart { get; set; }

        [JsonPropertyName("newCount")]
        public int NewCount { get; set; }

        [JsonPropertyName("lines")]
        public List<StructuredLine> Lines { get; set; } = new();
    }

    public class StructuredLine
    {
        [JsonPropertyName("op")]
        public string Op { get; set; } = string.Empty;

        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;
    }
}
