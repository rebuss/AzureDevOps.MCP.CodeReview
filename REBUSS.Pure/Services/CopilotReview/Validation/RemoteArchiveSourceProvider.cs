using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core;
using REBUSS.Pure.RoslynProcessor;

namespace REBUSS.Pure.Services.CopilotReview.Validation;

/// <summary>
/// PR-mode finding source provider. Reads the file's after-state from the downloaded
/// repository archive resolved via <see cref="IRepositoryDownloadOrchestrator"/>.
/// Behavior is intentionally aligned with the prior PR-mode validation path
/// (<see cref="DiffSourceResolver"/>): same 100&#160;KB cap, same 3-minute download
/// timeout, same null-on-not-found semantics. Used for any review key that is not
/// a <c>local:*</c> prefix.
/// </summary>
public sealed class RemoteArchiveSourceProvider : IFindingSourceProvider
{
    private const int MaxFileSizeBytes = 100 * 1024;
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(3);

    private readonly IRepositoryDownloadOrchestrator _orchestrator;
    private readonly ILogger<RemoteArchiveSourceProvider> _logger;

    public RemoteArchiveSourceProvider(
        IRepositoryDownloadOrchestrator orchestrator,
        ILogger<RemoteArchiveSourceProvider> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    public async Task<string?> GetAfterCodeAsync(string filePath, CancellationToken cancellationToken)
    {
        // 1. Wait for repository download with timeout. Linked CTS lets us distinguish
        //    caller cancellation (propagate) from timeout (return null + log Debug),
        //    matching DiffSourceResolver's contract.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(DownloadTimeout);

        string? extractedPath;
        try
        {
            extractedPath = await _orchestrator.GetExtractedPathAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Repository download timed out for {FilePath}, validation source unavailable", filePath);
            return null;
        }

        if (extractedPath is null)
            return null;

        // 2. Resolve file in extracted repository.
        var repoRoot = RepositoryFileResolver.ResolveRoot(extractedPath);
        var resolvedPath = RepositoryFileResolver.ResolvePath(repoRoot, filePath);
        if (resolvedPath is null)
        {
            _logger.LogDebug("File {FilePath} not found in extracted repository", filePath);
            return null;
        }

        // 3. Size cap (parity with DiffSourceResolver / local provider).
        var fileInfo = new FileInfo(resolvedPath);
        if (fileInfo.Length > MaxFileSizeBytes)
        {
            _logger.LogDebug("File {FilePath} exceeds size limit ({Size} bytes), validation source unavailable", filePath, fileInfo.Length);
            return null;
        }

        // 4. Read after-state.
        try
        {
            return await File.ReadAllTextAsync(resolvedPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read {FilePath} from extracted repository", filePath);
            return null;
        }
    }
}
