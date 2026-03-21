namespace REBUSS.Pure.Core.Models
{
    /// <summary>
    /// Represents the full content of a file fetched from a repository at a specific Git ref.
    /// </summary>
    public class FileContent
    {
        public string Path { get; set; } = string.Empty;
        public string Ref { get; set; } = string.Empty;

        /// <summary>
        /// Size of the content in bytes (UTF-8 encoded).
        /// </summary>
        public int Size { get; set; }

        /// <summary>
        /// Encoding of <see cref="Content"/>: "utf-8" for text files, "base64" for binary files.
        /// </summary>
        public string Encoding { get; set; } = "utf-8";

        /// <summary>
        /// The file content. For text files this is the decoded text; for binary files it is base64-encoded.
        /// Null when the content could not be retrieved.
        /// </summary>
        public string? Content { get; set; }

        public bool IsBinary { get; set; }
    }
}
