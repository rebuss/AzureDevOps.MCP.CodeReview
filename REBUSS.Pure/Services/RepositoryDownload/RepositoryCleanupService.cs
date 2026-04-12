using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace REBUSS.Pure.Services.RepositoryDownload;

/// <summary>
/// Hosted service that cleans up orphaned repository directories from previous
/// crashed server sessions on startup. Uses PID-based directory naming to
/// distinguish orphaned directories from those owned by concurrent instances.
/// </summary>
public class RepositoryCleanupService : IHostedService
{
    private readonly ILogger<RepositoryCleanupService> _logger;

    public RepositoryCleanupService(ILogger<RepositoryCleanupService> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        CleanupOrphanedDirectories();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Shutdown cleanup is handled by RepositoryDownloadOrchestrator via IHostApplicationLifetime
        return Task.CompletedTask;
    }

    private void CleanupOrphanedDirectories()
    {
        try
        {
            var tempDir = Path.GetTempPath();
            var prefix = RepositoryDownloadOrchestrator.TempRootPrefix;
            var currentPid = Environment.ProcessId;

            var candidates = Directory.EnumerateDirectories(tempDir, $"{prefix}*");
            foreach (var dir in candidates)
            {
                var dirName = Path.GetFileName(dir);
                if (!TryExtractPid(dirName, prefix, out var pid))
                    continue;

                if (pid == currentPid)
                    continue;

                if (IsProcessRunning(pid))
                {
                    _logger.LogDebug(
                        "Skipping {Directory} — process {Pid} is still running",
                        dirName, pid);
                    continue;
                }

                _logger.LogInformation("Deleting orphaned repository directory {Directory} (PID {Pid} no longer running)", dirName, pid);
                TryDeleteDirectory(dir);
            }

            // Also clean up orphaned ZIP files matching the pattern
            var zipCandidates = Directory.EnumerateFiles(tempDir, $"{prefix}*.zip");
            foreach (var zipFile in zipCandidates)
            {
                var fileName = Path.GetFileNameWithoutExtension(zipFile);
                if (!TryExtractPid(fileName, prefix, out var zipPid))
                    continue;

                if (zipPid == currentPid)
                    continue;

                if (IsProcessRunning(zipPid))
                {
                    _logger.LogDebug(
                        "Skipping ZIP {FileName} — process {Pid} is still running",
                        Path.GetFileName(zipFile), zipPid);
                    continue;
                }

                _logger.LogInformation("Deleting orphaned ZIP file {FileName} (PID {Pid} no longer running)", Path.GetFileName(zipFile), zipPid);
                TryDeleteFile(zipFile);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during orphaned repository cleanup on startup");
        }
    }

    internal static bool TryExtractPid(string dirName, string prefix, out int pid)
    {
        pid = 0;
        if (!dirName.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        return int.TryParse(dirName[prefix.Length..], out pid);
    }

    private static bool IsProcessRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // Process does not exist
            return false;
        }
        catch
        {
            // Access denied or other error — assume running to be safe
            return true;
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete orphaned directory {Path}", path);
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete orphaned file {Path}", path);
        }
    }
}
