namespace REBUSS.Pure.Core.Models
{
    /// <summary>
    /// Aggregated result of all changed files in a pull request,
    /// including per-file details and a category summary.
    /// </summary>
    public sealed class PullRequestFiles
    {
        public List<PullRequestFileInfo> Files { get; set; } = new();
        public PullRequestFilesSummary Summary { get; set; } = new();
    }

    /// <summary>
    /// Detailed information about a single changed file in a PR.
    /// </summary>
    public sealed class PullRequestFileInfo
    {
        public string Path { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int Additions { get; set; }
        public int Deletions { get; set; }
        public int Changes { get; set; }
        public string Extension { get; set; } = string.Empty;
        public bool IsBinary { get; set; }
        public bool IsGenerated { get; set; }
        public bool IsTestFile { get; set; }
        public string ReviewPriority { get; set; } = "medium";
    }

    /// <summary>
    /// Aggregated counts across the PR grouped by file category.
    /// </summary>
    public sealed class PullRequestFilesSummary
    {
        public int SourceFiles { get; set; }
        public int TestFiles { get; set; }
        public int ConfigFiles { get; set; }
        public int DocsFiles { get; set; }
        public int BinaryFiles { get; set; }
        public int GeneratedFiles { get; set; }
        public int HighPriorityFiles { get; set; }
    }
}
