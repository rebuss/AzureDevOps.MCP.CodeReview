namespace REBUSS.Pure.Tools.Models
{
    public class StructuredFileChange
    {
        public string Path { get; set; } = string.Empty;
        public string ChangeType { get; set; } = string.Empty;
        public string? SkipReason { get; set; }
        public int Additions { get; set; }
        public int Deletions { get; set; }
        public List<StructuredHunk> Hunks { get; set; } = new();
    }

    public class StructuredHunk
    {
        public int OldStart { get; set; }
        public int OldCount { get; set; }
        public int NewStart { get; set; }
        public int NewCount { get; set; }
        public List<StructuredLine> Lines { get; set; } = new();
    }

    public class StructuredLine
    {
        public string Op { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
    }
}