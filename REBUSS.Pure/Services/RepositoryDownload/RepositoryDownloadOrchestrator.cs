using System.IO.Compression;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.Services.RepositoryDownload;

/// <summary>
/// Manages the repository download lifecycle: deduplication, state tracking,
/// background execution, and cleanup on shutdown.
/// </summary>
public class RepositoryDownloadOrchestrator : IRepositoryDownloadOrchestrator
{
    internal static readonly string TempRootPrefix = "rebuss-repo-";

    private readonly IRepositoryArchiveProvider _archiveProvider;
    private readonly IEnumerable<IRepositoryReadyHandler> _readyHandlers;
    private readonly ILogger<RepositoryDownloadOrchestrator> _logger;
    private readonly CancellationToken _shutdownToken;
    private readonly object _lock = new();
    private readonly string _instanceDir;

    private RepositoryDownloadState _state = new();
    private CancellationTokenSource? _downloadCts;
    private long _runCounter;

    public RepositoryDownloadOrchestrator(
        IRepositoryArchiveProvider archiveProvider,
        IHostApplicationLifetime lifetime,
        IEnumerable<IRepositoryReadyHandler> readyHandlers,
        ILogger<RepositoryDownloadOrchestrator> logger)
    {
        _archiveProvider = archiveProvider;
        _readyHandlers = readyHandlers;
        _logger = logger;
        _shutdownToken = lifetime.ApplicationStopping;
        _instanceDir = Path.Combine(Path.GetTempPath(), $"{TempRootPrefix}{Environment.ProcessId}");

        lifetime.ApplicationStopping.Register(OnShutdown);
    }

    public void TriggerDownload(int prNumber, string commitRef)
    {
        lock (_lock)
        {
            // Same PR and commit — already in progress or done
            if (_state.PrNumber == prNumber &&
                _state.CommitRef == commitRef &&
                _state.Status is DownloadStatus.Downloading or DownloadStatus.Extracting or DownloadStatus.Ready)
            {
                return;
            }

            // Different PR or different commit — cancel old and start new
            if (_state.Status is DownloadStatus.Downloading or DownloadStatus.Extracting)
            {
                _logger.LogInformation(
                    "Cancelling in-progress download for PR #{OldPr} to start PR #{NewPr}",
                    _state.PrNumber, prNumber);
                _downloadCts?.Cancel();
                _downloadCts?.Dispose();
            }

            // Clean up old extracted directory if switching PRs
            if (_state.Status == DownloadStatus.Ready && _state.ExtractedPath != null)
            {
                TryDeleteDirectory(_state.ExtractedPath);
            }

            _downloadCts = new CancellationTokenSource();
            // Evaluate the token immediately (it's a struct, captured by value). Do NOT
            // capture the CancellationTokenSource via a closure — the next TriggerDownload
            // call may Dispose() it before Task.Run schedules the lambda, and `cts.Token`
            // on a disposed CTS throws ObjectDisposedException.
            var ctsToken = _downloadCts.Token;

            // Per-run unique paths so a superseded task and its replacement — possibly
            // sharing a prNumber with a different commit — cannot race each other on
            // Directory.Delete / ZipFile.ExtractToDirectory against the same directory.
            // The cancelled task cleans up *its own* paths in the OCE branch; the winning
            // task publishes its path to `_state.ExtractedPath` under the same lock that
            // arbitrates "am I still current?".
            var runId = Interlocked.Increment(ref _runCounter);
            var zipPath = Path.Combine(_instanceDir, $"{prNumber}-{runId}.zip");
            var extractDir = Path.Combine(_instanceDir, $"{prNumber}-{runId}");

            _state = new RepositoryDownloadState
            {
                PrNumber = prNumber,
                CommitRef = commitRef,
                Status = DownloadStatus.Downloading
            };

            _state.DownloadTask = Task.Run(() => ExecuteDownloadAsync(prNumber, commitRef, zipPath, extractDir, ctsToken));
        }
    }

    public RepositoryDownloadState GetState()
    {
        lock (_lock)
        {
            return _state;
        }
    }

    public async Task<string?> GetExtractedPathAsync(CancellationToken ct = default)
    {
        Task downloadTask;
        lock (_lock)
        {
            downloadTask = _state.DownloadTask;
        }

        try
        {
            await downloadTask.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        lock (_lock)
        {
            return _state.Status == DownloadStatus.Ready ? _state.ExtractedPath : null;
        }
    }

    private async Task ExecuteDownloadAsync(
        int prNumber, string commitRef, string zipPath, string extractDir, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(_instanceDir);

            _logger.LogInformation("Downloading repository ZIP for PR #{PrNumber} at {CommitRef}", prNumber, commitRef);

            await _archiveProvider.DownloadRepositoryZipAsync(commitRef, zipPath, ct);

            bool superseded;
            lock (_lock)
            {
                superseded = _state.PrNumber != prNumber || _state.CommitRef != commitRef;
                if (!superseded)
                    _state.Status = DownloadStatus.Extracting;
            }

            if (superseded)
            {
                // Superseded after download completed but before we could start extracting.
                // Clean up our own per-run zip so it does not linger in the instance
                // directory — the winning run has its own unique paths (see TriggerDownload).
                TryDeleteFile(zipPath);
                return;
            }

            _logger.LogInformation("Extracting repository ZIP for PR #{PrNumber}", prNumber);

            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, recursive: true);

            ZipFile.ExtractToDirectory(zipPath, extractDir);

            // Delete ZIP immediately after successful extraction
            TryDeleteFile(zipPath);

            lock (_lock)
            {
                superseded = _state.PrNumber != prNumber || _state.CommitRef != commitRef;
                if (!superseded)
                {
                    _state.Status = DownloadStatus.Ready;
                    _state.ExtractedPath = extractDir;
                }
            }

            if (superseded)
            {
                // Superseded between extract and publication — our extract artifact is
                // orphaned (the winning run uses its own extractDir). Clean it up.
                TryDeleteDirectory(extractDir);
                return;
            }

            _logger.LogInformation("Repository ready for PR #{PrNumber} at {ExtractDir}", prNumber, extractDir);

            foreach (var handler in _readyHandlers)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await handler.OnRepositoryReadyAsync(extractDir, prNumber, _shutdownToken);
                    }
                    catch (OperationCanceledException) { /* shutdown — OK */ }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Repository ready handler {Handler} failed for PR #{PrNumber}",
                            handler.GetType().Name, prNumber);
                    }
                }, _shutdownToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Repository download cancelled for PR #{PrNumber}", prNumber);
            TryDeleteFile(zipPath);
            TryDeleteDirectory(extractDir);

            lock (_lock)
            {
                if (_state.PrNumber == prNumber && _state.CommitRef == commitRef)
                {
                    _state.Status = DownloadStatus.Failed;
                    _state.ErrorMessage = "Download cancelled";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Repository download failed for PR #{PrNumber}", prNumber);
            TryDeleteFile(zipPath);
            TryDeleteDirectory(extractDir);

            lock (_lock)
            {
                if (_state.PrNumber == prNumber && _state.CommitRef == commitRef)
                {
                    _state.Status = DownloadStatus.Failed;
                    _state.ErrorMessage = ex.Message;
                }
            }
        }
    }

    private void OnShutdown()
    {
        _logger.LogInformation("Server shutting down — cleaning up repository download");
        _downloadCts?.Cancel();
        _downloadCts?.Dispose();
        TryDeleteDirectory(_instanceDir);
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete file {Path}", path);
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete directory {Path}", path);
        }
    }
}
