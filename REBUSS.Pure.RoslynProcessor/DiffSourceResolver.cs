using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core;

namespace REBUSS.Pure.RoslynProcessor;

/// <summary>
/// The before/after source code pair for a single file, resolved from the repository.
/// </summary>
/// <param name="FilePath">Relative file path from the diff header.</param>
/// <param name="BeforeCode">Reconstructed "before" source from the diff.</param>
/// <param name="AfterCode">Current source from the extracted repository.</param>
public sealed record DiffSourcePair(string FilePath, string BeforeCode, string AfterCode);

/// <summary>
/// Shared service that extracts before/after source code from the downloaded repository
/// for a given diff. Handles download wait, path resolution, size validation, and
/// before-code reconstruction. Used by multiple <see cref="Core.Shared.IDiffEnricher"/> implementations.
/// </summary>
public class DiffSourceResolver
{
    private const int MaxFileSizeBytes = 100 * 1024; // 100KB
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(3);

    private readonly IRepositoryDownloadOrchestrator _orchestrator;
    private readonly ILogger<DiffSourceResolver> _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task<DiffSourcePair?>>> _cache = new();

    public DiffSourceResolver(
        IRepositoryDownloadOrchestrator orchestrator,
        ILogger<DiffSourceResolver> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the before/after source code for the file in the given diff.
    /// Returns <c>null</c> if the file cannot be resolved (repo unavailable, file missing, too large, etc.).
    /// Results are cached per file path so that multiple enrichers share one resolution.
    /// </summary>
    public async Task<DiffSourcePair?> ResolveAsync(string diff, CancellationToken ct = default)
    {
        // 1. Parse file path from diff header
        var filePath = DiffParser.TryParseFilePath(diff);
        if (filePath == null)
            return null;

        // Cache lookup/insert: the Lazy<Task<>> pattern ensures only one resolution
        // per file path, even under concurrent access. The shared task runs with
        // CancellationToken.None so that one caller's cancellation cannot propagate
        // into the task body and fault the shared Task for all other waiters — each
        // caller gates its own wait via Task.WaitAsync(ct). If the shared task itself
        // is cancelled/faulted, evict so the next caller gets a fresh attempt.
        var lazy = _cache.GetOrAdd(filePath,
            _ => new Lazy<Task<DiffSourcePair?>>(() => ResolveInternalAsync(filePath, diff, CancellationToken.None)));

        try
        {
            return await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!lazy.Value.IsCanceled)
        {
            // Per-caller ct cancelled; the shared task is still running (or succeeded)
            // for other waiters — do not evict the cache entry.
            throw;
        }
        catch
        {
            _cache.TryRemove(filePath, out _);
            throw;
        }
    }

    private async Task<DiffSourcePair?> ResolveInternalAsync(
        string filePath, string diff, CancellationToken ct)
    {
        // 2. Wait for repository download with timeout
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(DownloadTimeout);

        string? repoPath;
        try
        {
            repoPath = await _orchestrator.GetExtractedPathAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("Repository download timed out for {FilePath}, skipping enrichment", filePath);
            return null;
        }

        if (repoPath == null)
            return null;

        // 3. Resolve file in extracted repository
        var repoRoot = RepositoryFileResolver.ResolveRoot(repoPath);
        var resolvedPath = RepositoryFileResolver.ResolvePath(repoRoot, filePath);
        if (resolvedPath == null)
        {
            _logger.LogDebug("File {FilePath} not found in extracted repository", filePath);
            return null;
        }

        // 4. Check file size
        var fileInfo = new FileInfo(resolvedPath);
        if (fileInfo.Length > MaxFileSizeBytes)
        {
            _logger.LogDebug("File {FilePath} exceeds size limit ({Size} bytes), skipping enrichment", filePath, fileInfo.Length);
            return null;
        }

        // 5. Read "after" source from repository
        var afterCode = await File.ReadAllTextAsync(resolvedPath, ct);

        // 6. Reconstruct "before" source from diff
        var beforeCode = ReconstructBeforeCode(diff);

        return new DiffSourcePair(filePath, beforeCode, afterCode);
    }

    /// <summary>
    /// Reconstructs the "before" code by extracting deleted and context lines from the diff.
    /// The source file in the repository represents the "after" state (PR head commit).
    /// </summary>
    private static string ReconstructBeforeCode(string diff)
    {
        var lines = diff.Replace("\r\n", "\n").Split('\n');
        var beforeLines = new List<string>();

        foreach (var line in lines)
        {
            if (line.StartsWith("===") || line.StartsWith("@@"))
                continue;

            if (line.StartsWith("+"))
                continue; // Added in "after", not in "before"

            if (line.StartsWith("-"))
                beforeLines.Add(line[1..]); // Removed line was in "before"
            else if (line.Length > 0 && line[0] == ' ')
                beforeLines.Add(line[1..]); // Context line exists in both
        }

        return string.Join("\n", beforeLines);
    }
}
