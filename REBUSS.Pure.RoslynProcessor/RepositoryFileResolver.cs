namespace REBUSS.Pure.RoslynProcessor;

/// <summary>
/// Resolves file paths within an extracted repository archive,
/// handling the nested root directory that ZIP archives typically contain.
/// </summary>
public static class RepositoryFileResolver
{
    /// <summary>
    /// Detects the effective root directory of an extracted archive.
    /// If the archive extracted into a single wrapper directory (e.g., <c>repo-abc123/</c>),
    /// returns that inner directory. Otherwise returns the original path.
    /// </summary>
    public static string ResolveRoot(string extractedPath)
    {
        if (!Directory.Exists(extractedPath))
            return extractedPath;

        var dirs = Directory.GetDirectories(extractedPath);
        var files = Directory.GetFiles(extractedPath);

        if (dirs.Length == 1 && files.Length == 0)
            return dirs[0];

        return extractedPath;
    }

    /// <summary>
    /// Resolves a diff file path to an absolute path within the repository root.
    /// Returns <c>null</c> if the file does not exist.
    /// </summary>
    public static string? ResolvePath(string rootPath, string diffFilePath)
    {
        var normalized = diffFilePath.TrimStart('/');
        var fullPath = Path.Combine(rootPath, normalized.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(fullPath) ? fullPath : null;
    }
}
