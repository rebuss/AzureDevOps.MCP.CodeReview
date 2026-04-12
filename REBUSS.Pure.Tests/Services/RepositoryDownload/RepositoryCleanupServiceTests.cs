using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using REBUSS.Pure.Services.RepositoryDownload;

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
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// Launches a short-lived process and returns its PID after it exits,
    /// guaranteeing the PID is not occupied.
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
        process.WaitForExit(TimeSpan.FromSeconds(5));
        return process.Id;
    }

    [Fact]
    public async Task StartAsync_DeletesOrphanedDirectories()
    {
        var orphanDir = Path.Combine(_tempDir, $"rebuss-repo-{_deadPid}");
        Directory.CreateDirectory(orphanDir);
        Directory.CreateDirectory(Path.Combine(orphanDir, "42"));

        await _service.StartAsync(CancellationToken.None);

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

            Assert.True(Directory.Exists(activeDir), "Active instance directory should be preserved");
        }
        finally
        {
            try { Directory.Delete(activeDir, true); } catch { }
        }
    }

    [Fact]
    public async Task StartAsync_NoOrphanedDirs_CompletesWithoutError()
    {
        // Just verify it doesn't throw when there's nothing to clean
        await _service.StartAsync(CancellationToken.None);
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
