using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO.Compression;

namespace REBUSS.Pure.AzureDevOps.Providers.Diff;

/// <summary>
/// Encapsulates the lifecycle of a temp directory holding the base + target
/// repository archives extracted side by side. Layout:
/// <code>
/// %TEMP%/rebuss-repo-{pid}/diff-{guid}/
///   ├─ base/      (extracted base-commit archive)
///   └─ target/    (extracted target-commit archive)
/// </code>
/// The parent <c>rebuss-repo-{pid}</c> directory mirrors what the
/// repository-cleanup service uses, so a sibling sweep on next start can reclaim
/// space if we crash mid-build. ZIPs are deleted right after extraction to free
/// disk; the entire temp tree is removed in <see cref="DisposeAsync"/>.
/// <para>
/// Created via the static <see cref="CreateAsync"/> factory which downloads both
/// archives in parallel, extracts them, and deletes the ZIPs synchronously before
/// returning. Always wrap in <c>await using</c>.
/// </para>
/// </summary>
internal sealed class ExtractedArchiveWorkspace : IAsyncDisposable
{
    private readonly string _instanceDir;
    private readonly ILogger _logger;

    public string BaseDir { get; }
    public string TargetDir { get; }

    // Internal so tests can construct a workspace pointing at pre-populated temp dirs
    // without exercising the network-bound CreateAsync path. Production code always
    // goes through CreateAsync.
    internal ExtractedArchiveWorkspace(string instanceDir, string baseDir, string targetDir, ILogger logger)
    {
        _instanceDir = instanceDir;
        BaseDir = baseDir;
        TargetDir = targetDir;
        _logger = logger;
    }

    public static async Task<ExtractedArchiveWorkspace> CreateAsync(
        AzureDevOpsRepositoryArchiveProvider archiveProvider,
        string baseCommit,
        string targetCommit,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var instanceDir = Path.Combine(
            Path.GetTempPath(),
            $"rebuss-repo-{Environment.ProcessId}",
            $"diff-{Guid.NewGuid():N}");
        var baseZip = Path.Combine(instanceDir, "base.zip");
        var baseDir = Path.Combine(instanceDir, "base");
        var targetZip = Path.Combine(instanceDir, "target.zip");
        var targetDir = Path.Combine(instanceDir, "target");

        Directory.CreateDirectory(instanceDir);

        // Defensive cleanup: until we hand the workspace back to the caller, the
        // `await using` on the consumer side cannot fire — so any exception from
        // download/extract here would orphan instanceDir. Mirror the try/finally
        // semantics of the pre-refactor BuildFileDiffsViaZipAsync.
        try
        {
            var sw = Stopwatch.StartNew();
            await Task.WhenAll(
                archiveProvider.DownloadRepositoryZipAsync(baseCommit, baseZip, cancellationToken),
                archiveProvider.DownloadRepositoryZipAsync(targetCommit, targetZip, cancellationToken));
            sw.Stop();

            logger.LogInformation(
                "Downloaded base + target archives in {ElapsedMs}ms (base={BaseSize}B, target={TargetSize}B)",
                sw.ElapsedMilliseconds,
                new FileInfo(baseZip).Length,
                new FileInfo(targetZip).Length);

            var extractSw = Stopwatch.StartNew();
            ZipFile.ExtractToDirectory(baseZip, baseDir);
            ZipFile.ExtractToDirectory(targetZip, targetDir);
            extractSw.Stop();

            // Free disk space — extracted trees are kept, ZIPs are not needed past this point.
            TryDelete(baseZip, logger);
            TryDelete(targetZip, logger);

            logger.LogDebug("Extracted archives in {ElapsedMs}ms", extractSw.ElapsedMilliseconds);

            return new ExtractedArchiveWorkspace(instanceDir, baseDir, targetDir, logger);
        }
        catch
        {
            // TryDeleteDirectory is best-effort + logs; never throws — safe to call from
            // a catch block without masking the original exception.
            TryDeleteDirectory(instanceDir, logger);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        TryDeleteDirectory(_instanceDir, _logger);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Resolves the absolute on-disk path to the given diff file path within an extracted
    /// repository archive. Tries the path directly first and, if absent, falls back to
    /// looking under a single wrapper directory (some archives nest the repository under
    /// a top-level folder named after the commit). Returns <c>null</c> when the file is
    /// not present in any layout — e.g. for added/deleted files which are missing from
    /// one side of the diff.
    /// </summary>
    internal static string? TryResolveFilePath(string rootPath, string diffFilePath)
    {
        if (!Directory.Exists(rootPath))
            return null;

        var normalized = diffFilePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);

        var direct = Path.Combine(rootPath, normalized);
        if (File.Exists(direct) && IsWithin(rootPath, direct))
            return direct;

        // Fallback: a single wrapper directory at the archive root (Azure DevOps does this
        // for some downloads). Only attempt the fallback if the wrapper layout is present
        // and would actually contain the requested path.
        var topLevelDirs = Directory.GetDirectories(rootPath);
        var topLevelFiles = Directory.GetFiles(rootPath);
        if (topLevelDirs.Length == 1 && topLevelFiles.Length == 0)
        {
            var wrapped = Path.Combine(topLevelDirs[0], normalized);
            if (File.Exists(wrapped) && IsWithin(rootPath, wrapped))
                return wrapped;
        }

        return null;
    }

    /// <summary>
    /// Defence-in-depth: rejects paths that, after resolving <c>..</c> segments / absolute
    /// roots, escape the extracted archive root. The diff paths originate from the Azure
    /// DevOps API (generally trusted) but we never blindly read from a path that canonicalizes
    /// outside <paramref name="rootPath"/>.
    /// </summary>
    private static bool IsWithin(string rootPath, string candidatePath)
    {
        var rootFull = Path.GetFullPath(rootPath);
        var candidateFull = Path.GetFullPath(candidatePath);

        if (!rootFull.EndsWith(Path.DirectorySeparatorChar))
            rootFull += Path.DirectorySeparatorChar;

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return candidateFull.StartsWith(rootFull, comparison);
    }

    private static void TryDelete(string path, ILogger logger)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to delete temp file {Path}", path);
        }
    }

    private static void TryDeleteDirectory(string path, ILogger logger)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to delete temp directory {Path}", path);
        }
    }
}
