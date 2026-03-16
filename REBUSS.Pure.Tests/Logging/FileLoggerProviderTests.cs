using Microsoft.Extensions.Logging;
using REBUSS.Pure.Logging;

namespace REBUSS.Pure.Tests.Logging;

public class FileLoggerProviderTests : IDisposable
{
    private readonly string _tempDir;

    public FileLoggerProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    /// <summary>
    /// Reads a log file that is still held open (write-locked) by the provider.
    /// Uses FileShare.ReadWrite so the open write handle does not block us.
    /// </summary>
    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }

    // -------------------------------------------------------------------------
    // File naming
    // -------------------------------------------------------------------------

    [Fact]
    public void Constructor_CreatesLogFileForToday()
    {
        var today = new DateTime(2025, 6, 15, 10, 0, 0);
        using var provider = new FileLoggerProvider(_tempDir, () => today);

        Assert.True(File.Exists(Path.Combine(_tempDir, "server-2025-06-15.log")));
    }

    [Fact]
    public void LogFilePath_ReturnsCorrectPattern()
    {
        var today = new DateTime(2025, 1, 3);
        using var provider = new FileLoggerProvider(_tempDir, () => today);

        var expected = Path.Combine(_tempDir, "server-2025-01-03.log");
        Assert.Equal(expected, provider.LogFilePath(today));
    }

    // -------------------------------------------------------------------------
    // Writing
    // -------------------------------------------------------------------------

    [Fact]
    public void Log_WritesLineToTodaysFile()
    {
        var today = new DateTime(2025, 6, 15, 9, 30, 0);
        using var provider = new FileLoggerProvider(_tempDir, () => today);
        var logger = provider.CreateLogger("TestCategory");

        logger.LogInformation("hello world");

        var content = ReadShared(provider.LogFilePath(today));
        Assert.Contains("hello world", content);
        Assert.Contains("TestCategory", content);
        Assert.Contains("Information", content);
    }

    [Fact]
    public void Log_IncludesTimestampPrefix()
    {
        var now = new DateTime(2025, 6, 15, 14, 22, 55, 123);
        using var provider = new FileLoggerProvider(_tempDir, () => now);
        var logger = provider.CreateLogger("Cat");

        logger.LogWarning("msg");

        var content = ReadShared(provider.LogFilePath(now.Date));
        Assert.Contains("2025-06-15 14:22:55.123", content);
    }

    [Fact]
    public void Log_BelowDebug_IsIgnored()
    {
        var today = new DateTime(2025, 6, 15);
        using var provider = new FileLoggerProvider(_tempDir, () => today);
        var logger = provider.CreateLogger("Cat");

        logger.Log(LogLevel.Trace, "trace message");

        var content = ReadShared(provider.LogFilePath(today));
        Assert.DoesNotContain("trace message", content);
    }

    // -------------------------------------------------------------------------
    // Daily roll-over
    // -------------------------------------------------------------------------

    [Fact]
    public void Log_RollsToNewFile_WhenDateChanges()
    {
        var day1 = new DateTime(2025, 6, 15, 23, 59, 0);
        var day2 = new DateTime(2025, 6, 16, 0, 1, 0);
        var current = day1;

        using var provider = new FileLoggerProvider(_tempDir, () => current);
        var logger = provider.CreateLogger("Cat");

        logger.LogInformation("day1 message");

        current = day2;
        logger.LogInformation("day2 message");

        // After roll-over day1 file is closed — read normally
        var content1 = File.ReadAllText(provider.LogFilePath(day1.Date));
        var content2 = ReadShared(provider.LogFilePath(day2.Date));

        Assert.Contains("day1 message", content1);
        Assert.DoesNotContain("day2 message", content1);
        Assert.Contains("day2 message", content2);
        Assert.DoesNotContain("day1 message", content2);
    }

    // -------------------------------------------------------------------------
    // Old log deletion
    // -------------------------------------------------------------------------

    [Fact]
    public void Constructor_DeletesLogsOlderThanRetainDays()
    {
        var today = new DateTime(2025, 6, 15);

        // Stale files (older than RetainDays = 3 ? cutoff is 2025-06-12)
        var stale1 = Path.Combine(_tempDir, "server-2025-06-11.log");
        var stale2 = Path.Combine(_tempDir, "server-2025-05-01.log");
        // Files within the retention window (>= cutoff)
        var keep1 = Path.Combine(_tempDir, "server-2025-06-12.log"); // exactly at cutoff
        var keep2 = Path.Combine(_tempDir, "server-2025-06-13.log");
        var keep3 = Path.Combine(_tempDir, "server-2025-06-14.log");

        File.WriteAllText(stale1, "old");
        File.WriteAllText(stale2, "old");
        File.WriteAllText(keep1, "keep");
        File.WriteAllText(keep2, "keep");
        File.WriteAllText(keep3, "keep");

        using var provider = new FileLoggerProvider(_tempDir, () => today);

        Assert.False(File.Exists(stale1), "file 4 days old should be deleted");
        Assert.False(File.Exists(stale2), "very old file should be deleted");
        Assert.True(File.Exists(keep1), "file at cutoff boundary (3 days old) should be kept");
        Assert.True(File.Exists(keep2), "file 2 days old should be kept");
        Assert.True(File.Exists(keep3), "file 1 day old should be kept");
    }

    [Fact]
    public void RollOver_DeletesStaleLogsAtRollover()
    {
        var day1 = new DateTime(2025, 6, 15);
        var current = day1;

        var staleFile = Path.Combine(_tempDir, "server-2025-06-10.log");
        File.WriteAllText(staleFile, "stale");

        using var provider = new FileLoggerProvider(_tempDir, () => current);

        // Advance past retention window to trigger deletion on roll-over
        current = day1.AddDays(5); // 2025-06-20
        provider.CreateLogger("Cat").LogInformation("trigger roll");

        Assert.False(File.Exists(staleFile), "stale file should be deleted on roll-over");
    }

    [Fact]
    public void DeleteOldLogs_IgnoresNonLogFiles()
    {
        var today = new DateTime(2025, 6, 15);
        var unrelated = Path.Combine(_tempDir, "config.json");
        var otherTxt = Path.Combine(_tempDir, "server-notes.txt");
        File.WriteAllText(unrelated, "{}");
        File.WriteAllText(otherTxt, "notes");

        using var provider = new FileLoggerProvider(_tempDir, () => today);

        Assert.True(File.Exists(unrelated));
        Assert.True(File.Exists(otherTxt));
    }

    // -------------------------------------------------------------------------
    // Constants
    // -------------------------------------------------------------------------

    [Fact]
    public void RetainDays_IsThree()
    {
        Assert.Equal(3, FileLoggerProvider.RetainDays);
    }
}
