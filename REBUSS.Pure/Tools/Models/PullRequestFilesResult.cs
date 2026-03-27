using System.Text.Json.Serialization;

namespace REBUSS.Pure.Tools.Models
{
    /// <summary>
    /// Structured JSON response model for the get_pr_files tool.
    /// </summary>
    public class PullRequestFilesResult
    {
        [JsonPropertyName("prNumber")]
        public int PrNumber { get; set; }

        [JsonPropertyName("totalFiles")]
        public int TotalFiles { get; set; }

        [JsonPropertyName("files")]
        public List<PullRequestFileItem> Files { get; set; } = new();

        [JsonPropertyName("summary")]
        public PullRequestFilesSummaryResult Summary { get; set; } = new();

        [JsonPropertyName("manifest")]
        public ContentManifestResult? Manifest { get; set; }
    }

    public class PullRequestFileItem
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("additions")]
        public int Additions { get; set; }

        [JsonPropertyName("deletions")]
        public int Deletions { get; set; }

        [JsonPropertyName("changes")]
        public int Changes { get; set; }

        [JsonPropertyName("extension")]
        public string Extension { get; set; } = string.Empty;

        [JsonPropertyName("isBinary")]
        public bool IsBinary { get; set; }

        [JsonPropertyName("isGenerated")]
        public bool IsGenerated { get; set; }

        [JsonPropertyName("isTestFile")]
        public bool IsTestFile { get; set; }

        [JsonPropertyName("reviewPriority")]
        public string ReviewPriority { get; set; } = "medium";
    }

    public class PullRequestFilesSummaryResult
    {
        [JsonPropertyName("sourceFiles")]
        public int SourceFiles { get; set; }

        [JsonPropertyName("testFiles")]
        public int TestFiles { get; set; }

        [JsonPropertyName("configFiles")]
        public int ConfigFiles { get; set; }

        [JsonPropertyName("docsFiles")]
        public int DocsFiles { get; set; }

        [JsonPropertyName("binaryFiles")]
        public int BinaryFiles { get; set; }

        [JsonPropertyName("generatedFiles")]
        public int GeneratedFiles { get; set; }

        [JsonPropertyName("highPriorityFiles")]
        public int HighPriorityFiles { get; set; }
    }
}
