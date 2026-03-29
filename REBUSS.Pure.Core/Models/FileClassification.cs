using REBUSS.Pure.Core.Properties;

namespace REBUSS.Pure.Core.Models
{
    /// <summary>
    /// Result of classifying a file by its path and extension.
    /// </summary>
    public sealed class FileClassification
    {
        public string Extension { get; init; } = string.Empty;
        public bool IsBinary { get; init; }
        public bool IsGenerated { get; init; }
        public bool IsTestFile { get; init; }
        public FileCategory Category { get; init; }
        public string ReviewPriority { get; init; } = Resources.ReviewPriorityMedium;
    }

    /// <summary>
    /// High-level category of a file, used for summary aggregation.
    /// </summary>
    public enum FileCategory
    {
        Source,
        Test,
        Config,
        Docs,
        Binary,
        Generated
    }
}
