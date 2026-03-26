namespace REBUSS.Pure.Core.Models
{
    /// <summary>
    /// Represents a Pull Request diff with all relevant information.
    /// </summary>
    public class PullRequestDiff
    {
        public string Title { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string SourceBranch { get; set; } = string.Empty;
        public string TargetBranch { get; set; } = string.Empty;
        public string SourceRefName { get; set; } = string.Empty;
        public string TargetRefName { get; set; } = string.Empty;
        public List<FileChange> Files { get; set; } = new();
    }

    /// <summary>
    /// Represents a single file change in a PR.
    /// </summary>
    public class FileChange
    {
        public string Path { get; set; } = string.Empty;
        public string ChangeType { get; set; } = string.Empty;
        public List<DiffHunk> Hunks { get; set; } = new();

        /// <summary>
        /// When set, indicates that diff generation was skipped and explains why
        /// (e.g. "file deleted", "binary file", "generated file", "file renamed", "full file rewrite").
        /// <c>null</c> means the diff was computed normally.
        /// </summary>
        public string? SkipReason { get; set; }

        public int Additions { get; set; }
        public int Deletions { get; set; }
    }

    /// <summary>
    /// Represents a single hunk in a structured diff.
    /// </summary>
    public class DiffHunk
    {
        public int OldStart { get; set; }
        public int OldCount { get; set; }
        public int NewStart { get; set; }
        public int NewCount { get; set; }
        public List<DiffLine> Lines { get; set; } = new();
    }

    /// <summary>
    /// Represents a single line operation within a diff hunk.
    /// </summary>
    public class DiffLine
    {
        /// <summary>
        /// ' ' = unchanged context, '-' = removed, '+' = added.
        /// </summary>
        public char Op { get; set; }

        public string Text { get; set; } = string.Empty;
    }
}
