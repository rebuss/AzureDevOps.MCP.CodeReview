using System.Text.RegularExpressions;

namespace REBUSS.Pure.Core.Shared;

/// <summary>
/// Programming languages detectable from diff file paths.
/// </summary>
public enum DiffLanguage
{
    CSharp,
    TypeScript,
    JavaScript,
    Python,
    Go,
    Java,
    Rust,
    Kotlin,
    Ruby,
    Swift,
    Php,
    Cpp,
    Unknown
}

/// <summary>
/// Centralized language detection for diffs. Identifies the programming language
/// from the diff header file path. Used by enrichers for applicability checks.
/// </summary>
public static partial class DiffLanguageDetector
{
    // Same pattern as DiffParser.TryParseFilePath — duplicated to avoid Core→RoslynProcessor dependency.
    private static readonly Regex FilePathRegex = FilePathPattern();
    private static readonly Regex ScopeAnnotationRegex = ScopeAnnotationPattern();

    [GeneratedRegex(@"^=== (.+?) \(", RegexOptions.Multiline)]
    private static partial Regex FilePathPattern();

    // Matches an `@@ ` hunk header line that ends with a `[scope: …]` annotation.
    [GeneratedRegex(@"^@@ .*\[scope: ", RegexOptions.Multiline)]
    private static partial Regex ScopeAnnotationPattern();

    /// <summary>
    /// Detects the programming language from the diff header file path.
    /// </summary>
    public static DiffLanguage Detect(string diff)
    {
        if (string.IsNullOrEmpty(diff))
            return DiffLanguage.Unknown;

        var match = FilePathRegex.Match(diff);
        if (!match.Success)
            return DiffLanguage.Unknown;

        var filePath = match.Groups[1].Value;
        var extension = Path.GetExtension(filePath).ToLowerInvariant();

        return extension switch
        {
            ".cs" => DiffLanguage.CSharp,
            ".ts" or ".tsx" => DiffLanguage.TypeScript,
            ".js" or ".jsx" or ".mjs" or ".cjs" => DiffLanguage.JavaScript,
            ".py" => DiffLanguage.Python,
            ".go" => DiffLanguage.Go,
            ".java" => DiffLanguage.Java,
            ".rs" => DiffLanguage.Rust,
            ".kt" or ".kts" => DiffLanguage.Kotlin,
            ".rb" => DiffLanguage.Ruby,
            ".swift" => DiffLanguage.Swift,
            ".php" => DiffLanguage.Php,
            ".cpp" or ".cc" or ".cxx" or ".hpp" or ".h" => DiffLanguage.Cpp,
            _ => DiffLanguage.Unknown
        };
    }

    /// <summary>Returns <c>true</c> if the diff is for a C# file.</summary>
    public static bool IsCSharp(string diff) => Detect(diff) == DiffLanguage.CSharp;

    /// <summary>Returns <c>true</c> if the diff is marked as skipped.</summary>
    public static bool IsSkipped(string diff) => diff.Contains("skipped) ===");

    /// <summary>
    /// Returns <c>true</c> if the diff already carries any of the enricher-emitted markers
    /// (<c>[scope: …]</c> on a hunk header, or any of the file-level
    /// <c>[structural-changes]</c> / <c>[dependency-changes]</c> / <c>[call-sites]</c> blocks).
    /// Used by <c>CompositeCodeProcessor</c> as a centralized idempotence short-circuit so
    /// that running the enricher chain on its own output is a no-op (feature 011).
    /// </summary>
    public static bool IsAlreadyEnriched(string diff)
    {
        if (string.IsNullOrEmpty(diff)) return false;
        if (ScopeAnnotationRegex.IsMatch(diff)) return true;
        if (diff.Contains("[structural-changes]", StringComparison.Ordinal)) return true;
        if (diff.Contains("[dependency-changes]", StringComparison.Ordinal)) return true;
        if (diff.Contains("[call-sites]", StringComparison.Ordinal)) return true;
        return false;
    }
}
