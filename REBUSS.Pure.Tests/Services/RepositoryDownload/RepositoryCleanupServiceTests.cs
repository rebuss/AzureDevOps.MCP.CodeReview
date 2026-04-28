using Microsoft.Extensions.Logging.Abstractions;
using REBUSS.Pure.Services.RepositoryDownload;
using System.Diagnostics;

namespace REBUSS.Pure.Tests.Services.RepositoryDownload;

public class RepositoryCleanupServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly RepositoryCleanupService _service;
    private readonly int _deadPid;

    public RepositoryCleanupServiceTests()
    {
        _tempDir = Path.GetTempPath();
        _service = new RepositoryCleanupService(
            NullLogger<RepositoryCleanupService>.Instance);
        _deadPid = GetDeadPid();
    }

    public void Dispose()
    {
        // Best-effort cleanup of any directories we created
        foreach (var dir in Directory.EnumerateDirectories(_tempDir, $"rebuss-repo-{_deadPid}*"))
        {
            try { Directory.Delete(dir, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Launches a short-lived process and returns its PID once the process has exited —
    /// guaranteeing the PID is not occupied. If the process refuses to exit within the
    /// wait window, kill it (and the tree) before handing the PID back; if even the
    /// forced exit times out, throw rather than return a potentially-live PID, which
    /// would either flake the cleanup assertions or risk deleting another process's
    /// directories.
    /// </summary>
    private static int GetDeadPid()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? "dotnet",
            Arguments = "--version",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

        if (!process.WaitForExit(TimeSpan.FromSeconds(5)))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exiting */ }

            if (!process.WaitForExit(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException(
                    $"Probe process (PID {process.Id}) did not exit after forced kill; refusing to hand out a possibly-live PID to the cleanup test.");
        }

        return process.Id;
    }

    [Fact]
    public async Task StartAsync_DeletesOrphanedDirectories()
    {
        var orphanDir = Path.Combine(_tempDir, $"rebuss-repo-{_deadPid}");
        Directory.CreateDirectory(orphanDir);
        Directory.CreateDirectory(Path.Combine(orphanDir, "42"));

        await _service.StartAsync(CancellationToken.None);
        // Cleanup runs on a background task so it does not block startup — wait for it
        // via StopAsync, which returns the in-flight cleanup task.
        await _service.StopAsync(CancellationToken.None);

        Assert.False(Directory.Exists(orphanDir), "Orphaned directory should be deleted");
    }

    [Fact]
    public async Task StartAsync_PreservesActiveInstanceDirectories()
    {
        // Current process PID — should NOT be deleted
        var currentPid = Environment.ProcessId;
        var activeDir = Path.Combine(_tempDir, $"rebuss-repo-{currentPid}");
        Directory.CreateDirectory(activeDir);

        try
        {
            await _service.StartAsync(CancellationToken.None);
            await _service.StopAsync(CancellationToken.None);

            Assert.True(Directory.Exists(activeDir), "Active instance directory should be preserved");
        }
        finally
        {
            try { Directory.Delete(activeDir, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task StartAsync_NoOrphanedDirs_CompletesWithoutError()
    {
        // Just verify it doesn't throw when there's nothing to clean
        await _service.StartAsync(CancellationToken.None);
        await _service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_DoesNotBlockOnCleanup()
    {
        // Regression: cleanup work (Directory.EnumerateDirectories, Process.GetProcessById,
        // recursive Directory.Delete) must not run inline on the hosted-services pipeline.
        // StartAsync should return promptly regardless of the eventual cleanup cost.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await _service.StartAsync(CancellationToken.None);
        sw.Stop();

        // Generous bound — we're catching inline synchronous execution, not timing a fast path.
        Assert.True(sw.ElapsedMilliseconds < 200,
            $"StartAsync should return quickly but took {sw.ElapsedMilliseconds}ms — cleanup is likely running inline");

        // Drain the background work so the test doesn't leave stray tasks running.
        await _service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void TryExtractPid_ValidName_ReturnsTrueAndPid()
    {
        Assert.True(RepositoryCleanupService.TryExtractPid("rebuss-repo-12345", "rebuss-repo-", out var pid));
        Assert.Equal(12345, pid);
    }

    [Fact]
    public void TryExtractPid_InvalidName_ReturnsFalse()
    {
        Assert.False(RepositoryCleanupService.TryExtractPid("rebuss-repo-abc", "rebuss-repo-", out _));
    }

    [Fact]
    public void TryExtractPid_WrongPrefix_ReturnsFalse()
    {
        Assert.False(RepositoryCleanupService.TryExtractPid("other-dir-123", "rebuss-repo-", out _));
    }
}
