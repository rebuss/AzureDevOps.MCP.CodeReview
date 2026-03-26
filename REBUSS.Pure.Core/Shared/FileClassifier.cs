using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.Core.Shared;

/// <summary>
/// Determines file type, category, and review priority based on the file path.
/// All heuristics are centralised here so they can be adjusted in one place.
/// </summary>
public class FileClassifier : IFileClassifier
{
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".pdb", ".lib", ".obj", ".o", ".so", ".dylib",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".svg", ".webp",
        ".zip", ".tar", ".gz", ".7z", ".rar", ".nupkg",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx",
        ".woff", ".woff2", ".ttf", ".eot",
        ".class", ".jar", ".war", ".ear"
    };

    private static readonly HashSet<string> ConfigExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".xml", ".yaml", ".yml", ".config", ".ini", ".env", ".toml",
        ".props", ".targets", ".csproj", ".vbproj", ".fsproj", ".sln",
        ".editorconfig", ".gitignore", ".gitattributes", ".dockerignore",
        ".ruleset", ".nuspec"
    };

    private static readonly HashSet<string> DocsExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".rst", ".adoc", ".html", ".htm"
    };

    private static readonly string[] GeneratedPathPatterns =
    {
        "/obj/", "/bin/", "node_modules/",
        ".designer.", ".generated.", ".g.cs", ".g.i.cs"
    };

    private static readonly string[] GeneratedFileNames =
    {
        "assemblyinfo.cs", "globalusings.g.cs",
        "package-lock.json", "yarn.lock", "packages.lock.json"
    };

    private static readonly string[] TestPathPatterns =
    {
        "/test/", "/tests/", "/spec/", "/specs/", "/__tests__/",
        ".tests/", ".test/"
    };

    private static readonly string[] TestFilePatterns =
    {
        "tests.", "test.", "spec.",
        ".test.", ".spec.", ".tests.",
        "_test.", "_tests."
    };

    public FileClassification Classify(string path)
    {
        var normalizedPath = path.Replace('\\', '/').ToLowerInvariant();
        var extension = Path.GetExtension(path);
        var fileName = Path.GetFileName(normalizedPath);

        var isBinary = IsBinaryFile(extension);
        var isGenerated = IsGeneratedFile(normalizedPath, fileName);
        var isTestFile = IsTestFilePath(normalizedPath, fileName);

        var category = DetermineCategory(extension, isBinary, isGenerated, isTestFile, normalizedPath);
        var priority = DetermineReviewPriority(category);

        return new FileClassification
        {
            Extension = extension,
            IsBinary = isBinary,
            IsGenerated = isGenerated,
            IsTestFile = isTestFile,
            Category = category,
            ReviewPriority = priority
        };
    }

    private static bool IsBinaryFile(string extension) =>
        !string.IsNullOrEmpty(extension) && BinaryExtensions.Contains(extension);

    private static bool IsGeneratedFile(string normalizedPath, string fileName) =>
        GeneratedPathPatterns.Any(p => normalizedPath.Contains(p)) ||
        GeneratedFileNames.Any(n => fileName == n);

    private static bool IsTestFilePath(string normalizedPath, string fileName) =>
        TestPathPatterns.Any(p => normalizedPath.Contains(p)) ||
        TestFilePatterns.Any(p => fileName.Contains(p));

    private static FileCategory DetermineCategory(
        string extension, bool isBinary, bool isGenerated, bool isTestFile, string normalizedPath)
    {
        if (isBinary) return FileCategory.Binary;
        if (isGenerated) return FileCategory.Generated;
        if (isTestFile) return FileCategory.Test;
        if (!string.IsNullOrEmpty(extension) && ConfigExtensions.Contains(extension))
            return FileCategory.Config;
        if (IsDocsFile(extension, normalizedPath)) return FileCategory.Docs;
        return FileCategory.Source;
    }

    private static bool IsDocsFile(string extension, string normalizedPath) =>
        (!string.IsNullOrEmpty(extension) && DocsExtensions.Contains(extension)) ||
        normalizedPath.Contains("/docs/") ||
        normalizedPath.Contains("/documentation/");

    private static string DetermineReviewPriority(FileCategory category) => category switch
    {
        FileCategory.Source => "high",
        FileCategory.Test => "medium",
        FileCategory.Config => "medium",
        FileCategory.Docs => "low",
        FileCategory.Binary => "low",
        FileCategory.Generated => "low",
        _ => "medium"
    };
}
