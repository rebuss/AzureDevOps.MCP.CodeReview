using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using REBUSS.Pure.Services.CopilotReview.Inspection;
using System.Runtime.InteropServices;

namespace REBUSS.Pure.Tests.Services.CopilotReview.Inspection;

/// <summary>
/// Tests for <see cref="FileSystemAgentInspectionWriter"/>. Feature 022.
/// All tests use a temp base directory — never the real %LOCALAPPDATA%.
/// </summary>
public class FileSystemAgentInspectionWriterTests : IDisposable
{
    private readonly string _tempDir;

    public FileSystemAgentInspectionWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"copilot-inspection-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private FileSystemAgentInspectionWriter CreateWriter() =>
        new(_tempDir, NullLogger<FileSystemAgentInspectionWriter>.Instance);

    [Fact]
    public async Task WritePromptAsync_FirstCall_CreatesPerPrSubdirectoryAndFile()
    {
        var writer = CreateWriter();

        await writer.WritePromptAsync("pr:42", "page-1-review", "hello", CancellationToken.None);

        var prDir = Path.Combine(_tempDir, "pr-42");
        Assert.True(Directory.Exists(prDir));
        var files = Directory.GetFiles(prDir);
        Assert.Single(files);
    }

    [Fact]
    public async Task WritePromptAsync_FileNameMatchesFormat()
    {
        var writer = CreateWriter();

        await writer.WritePromptAsync("pr:42", "page-1-review", "hello", CancellationToken.None);

        var file = Directory.GetFiles(Path.Combine(_tempDir, "pr-42")).Single();
        var name = Path.GetFileName(file);
        // Format: yyyyMMdd-HHmmss-fff-NNN-kind-role.md
        Assert.Matches(@"^\d{8}-\d{6}-\d{3}-\d{3}-page-1-review-prompt\.md$", name);
    }

    [Fact]
    public async Task WritePromptAsync_ContentIsByteIdentical()
    {
        var writer = CreateWriter();
        const string content = "Line 1\nLine 2\n=== src/Foo.cs ===\n@@ -1 +1 @@\n-old\n+new";

        await writer.WritePromptAsync("pr:42", "page-1-review", content, CancellationToken.None);

        var file = Directory.GetFiles(Path.Combine(_tempDir, "pr-42")).Single();
        var readBack = await File.ReadAllTextAsync(file);
        Assert.Equal(content, readBack);
    }

    [Fact]
    public async Task WriteResponseAsync_AfterPrompt_CreatesSecondFileWithIncrementedSeq()
    {
        var writer = CreateWriter();

        await writer.WritePromptAsync("pr:42", "page-1-review", "p", CancellationToken.None);
        await writer.WriteResponseAsync("pr:42", "page-1-review", "r", CancellationToken.None);

        var files = Directory.GetFiles(Path.Combine(_tempDir, "pr-42")).Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.Equal(2, files.Length);
        Assert.EndsWith("-001-page-1-review-prompt.md", files[0]);
        Assert.EndsWith("-002-page-1-review-response.md", files[1]);
    }

    [Fact]
    public async Task MultipleWrites_SameKey_AllLandInOneSubdirectory()
    {
        var writer = CreateWriter();

        for (var i = 1; i <= 5; i++)
            await writer.WritePromptAsync("pr:42", $"page-{i}-review", $"p{i}", CancellationToken.None);

        var prDir = Path.Combine(_tempDir, "pr-42");
        var files = Directory.GetFiles(prDir);
        Assert.Equal(5, files.Length);
        // No sibling pr-42_* directories
        var allDirs = Directory.GetDirectories(_tempDir);
        Assert.Single(allDirs);
    }

    [Fact]
    public async Task MultipleWrites_DifferentKeys_GetSeparateSubdirectories()
    {
        var writer = CreateWriter();

        await writer.WritePromptAsync("pr:42", "page-1-review", "a", CancellationToken.None);
        await writer.WritePromptAsync("pr:99", "page-1-review", "b", CancellationToken.None);
        await writer.WritePromptAsync("local:staged:/repo", "page-1-review", "c", CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(_tempDir, "pr-42")));
        Assert.True(Directory.Exists(Path.Combine(_tempDir, "pr-99")));
        Assert.True(Directory.Exists(Path.Combine(_tempDir, "local-staged-repo")));
    }

    [Fact]
    public async Task IllegalCharsInReviewKey_Sanitized()
    {
        var writer = CreateWriter();

        await writer.WritePromptAsync("pr:42", "page-1-review", "x", CancellationToken.None);

        // No directory with a colon
        Assert.False(Directory.Exists(Path.Combine(_tempDir, "pr:42")));
        Assert.True(Directory.Exists(Path.Combine(_tempDir, "pr-42")));
    }

    [Fact]
    public async Task PathTraversalInReviewKey_StaysUnderBase()
    {
        var writer = CreateWriter();

        await writer.WritePromptAsync("../../etc/passwd", "page-1-review", "x", CancellationToken.None);

        // Containment check: every file the writer produced must be under _tempDir.
        var tempFullPath = Path.GetFullPath(_tempDir);
        var allFiles = Directory.GetFiles(tempFullPath, "*", SearchOption.AllDirectories);
        foreach (var f in allFiles)
        {
            var full = Path.GetFullPath(f);
            Assert.StartsWith(tempFullPath, full, StringComparison.OrdinalIgnoreCase);
        }
        Assert.NotEmpty(allFiles);
    }

    [Fact]
    public async Task ParallelWrites_100Threads_NoCollisionOrLostFile()
    {
        var writer = CreateWriter();

        var tasks = Enumerable.Range(0, 100)
            .Select(i => writer.WritePromptAsync("pr:42", $"page-{i}-review", $"p{i}", CancellationToken.None))
            .ToArray();
        await Task.WhenAll(tasks);

        var files = Directory.GetFiles(Path.Combine(_tempDir, "pr-42"));
        Assert.Equal(100, files.Length);
        // All file names must be unique
        Assert.Equal(100, files.Select(Path.GetFileName).Distinct().Count());
    }

    [Fact]
    public async Task IOException_LoggedAndReturnsNormally()
    {
        // Delete the base directory, then attempt a write into a path that can't be created
        // (simulate by creating a file at the same location as the expected subdirectory).
        var logger = Substitute.For<ILogger<FileSystemAgentInspectionWriter>>();
        var writer = new FileSystemAgentInspectionWriter(_tempDir, logger);

        // Create a *file* at the spot where the PR subdirectory would land → CreateDirectory fails.
        var conflictPath = Path.Combine(_tempDir, "pr-42");
        File.WriteAllText(conflictPath, "block");

        // Must not throw.
        await writer.WritePromptAsync("pr:42", "page-1-review", "x", CancellationToken.None);

        // A warning must have been logged.
        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task Cancellation_PropagatesOperationCanceledException()
    {
        var writer = CreateWriter();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await writer.WritePromptAsync("pr:42", "page-1-review", "x", cts.Token));
    }

    // Sanitize() tests — verify the normalization pipeline directly.

    [Theory]
    [InlineData("pr:42", "pr-42")]
    [InlineData("pr-42", "pr-42")]
    [InlineData("local:staged:/repo/foo", "local-staged-repo-foo")]
    [InlineData("", "review")]
    [InlineData("///", "review")]
    [InlineData(":::", "review")]
    [InlineData("...", "review")]
    [InlineData("-a-", "a")]
    [InlineData("foo..bar", "foo..bar")] // dots inside are kept; only trimmed from ends
    public void Sanitize_NormalizesExpectedCases(string input, string expected)
    {
        var actual = FileSystemAgentInspectionWriter.Sanitize(input);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Sanitize_LongInput_CappedAt100Chars()
    {
        var longInput = new string('a', 200);
        var result = FileSystemAgentInspectionWriter.Sanitize(longInput);
        Assert.Equal(100, result.Length);
    }

    // ─── Feature 022 US3 (T020) — retention ───────────────────────────────────────

    /// <summary>
    /// Polls until <paramref name="condition"/> is true or <paramref name="timeoutMs"/>
    /// elapses. Used to wait for fire-and-forget cleanup without flaky fixed delays.
    /// </summary>
    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var start = Environment.TickCount;
        while (Environment.TickCount - start < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    [Fact]
    public async Task Cleanup_FileOlderThan24h_IsDeleted()
    {
        var prDir = Path.Combine(_tempDir, "pr-1");
        Directory.CreateDirectory(prDir);
        var oldFile = Path.Combine(prDir, "20260412-000000-000-001-page-1-review-prompt.md");
        await File.WriteAllTextAsync(oldFile, "old content");

        // Constructing the writer kicks off cleanup.
        _ = CreateWriter();

        var deleted = await WaitForAsync(() => !File.Exists(oldFile));
        Assert.True(deleted, "Old file should have been removed by cleanup");
    }

    [Fact]
    public async Task Cleanup_FileYoungerThan24h_IsPreserved()
    {
        var prDir = Path.Combine(_tempDir, "pr-2");
        Directory.CreateDirectory(prDir);
        var freshTs = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var freshFile = Path.Combine(prDir, $"{freshTs}-001-page-1-review-prompt.md");
        await File.WriteAllTextAsync(freshFile, "fresh content");

        // Sentinel old file in a sibling subdir — its deletion is the observable signal
        // that cleanup has completed, avoiding flaky fixed Task.Delay waits (fast CI
        // would waste the delay; slow CI might not have started cleanup yet).
        var sentinelDir = Path.Combine(_tempDir, "pr-2-sentinel");
        Directory.CreateDirectory(sentinelDir);
        var sentinel = Path.Combine(sentinelDir, "20260412-000000-000-001-prompt.md");
        await File.WriteAllTextAsync(sentinel, "sentinel");

        _ = CreateWriter();

        Assert.True(await WaitForAsync(() => !File.Exists(sentinel)),
            "Sentinel old file should have been removed — cleanup did not run");
        Assert.True(File.Exists(freshFile), "Fresh file should be preserved");
    }

    [Fact]
    public async Task Cleanup_MixedAgesInSameSubdir_OldDeletedFreshKept()
    {
        var prDir = Path.Combine(_tempDir, "pr-3");
        Directory.CreateDirectory(prDir);
        var oldFile = Path.Combine(prDir, "20260412-000000-000-001-page-1-review-prompt.md");
        var freshTs = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var freshFile = Path.Combine(prDir, $"{freshTs}-002-page-1-review-response.md");
        await File.WriteAllTextAsync(oldFile, "old");
        await File.WriteAllTextAsync(freshFile, "fresh");

        _ = CreateWriter();

        await WaitForAsync(() => !File.Exists(oldFile));
        Assert.False(File.Exists(oldFile), "Old file should be removed");
        Assert.True(File.Exists(freshFile), "Fresh file should be preserved");
        Assert.True(Directory.Exists(prDir), "Subdir should remain because a fresh file is still inside");
    }

    [Fact]
    public async Task Cleanup_AllFilesOldInSubdir_EmptySubdirRemoved()
    {
        var prDir = Path.Combine(_tempDir, "pr-4");
        Directory.CreateDirectory(prDir);
        await File.WriteAllTextAsync(Path.Combine(prDir, "20260412-000000-000-001-prompt.md"), "old1");
        await File.WriteAllTextAsync(Path.Combine(prDir, "20260412-000005-000-002-response.md"), "old2");

        _ = CreateWriter();

        var dirGone = await WaitForAsync(() => !Directory.Exists(prDir));
        Assert.True(dirGone, "Empty subdir (after file removal) should be deleted");
    }

    [Fact]
    public async Task Cleanup_FreshSubdir_PreservedEvenWhenInitiallyEmpty()
    {
        // This is the regression test for the race fix: a brand-new empty subdir created
        // by the writer must NOT be deleted by cleanup.
        var prDir = Path.Combine(_tempDir, "pr-5");
        Directory.CreateDirectory(prDir);
        // No files inside — simulates the moment after CreateDirectory but before WriteAllText.

        // Sentinel old file in a sibling subdir — its deletion signals cleanup has run,
        // so we assert preservation on a completed cleanup rather than on a fixed wait.
        var sentinelDir = Path.Combine(_tempDir, "pr-5-sentinel");
        Directory.CreateDirectory(sentinelDir);
        var sentinel = Path.Combine(sentinelDir, "20260412-000000-000-001-prompt.md");
        await File.WriteAllTextAsync(sentinel, "sentinel");

        _ = CreateWriter();

        Assert.True(await WaitForAsync(() => !File.Exists(sentinel)),
            "Sentinel old file should have been removed — cleanup did not run");
        Assert.True(Directory.Exists(prDir),
            "Brand-new empty subdir (no prior files) must not be touched by cleanup");
    }

    [Fact]
    public async Task Cleanup_UnparseableFileName_UsesCreationTime()
    {
        var prDir = Path.Combine(_tempDir, "pr-6");
        Directory.CreateDirectory(prDir);
        var garbledFile = Path.Combine(prDir, "garbled-file-name-no-timestamp.md");
        await File.WriteAllTextAsync(garbledFile, "x");
        // Backdate the creation time to 48h ago.
        File.SetCreationTimeUtc(garbledFile, DateTime.UtcNow.AddHours(-48));

        _ = CreateWriter();

        var deleted = await WaitForAsync(() => !File.Exists(garbledFile));
        Assert.True(deleted, "Garbled-name file with old CreationTimeUtc should be removed");
    }

    [Fact]
    public async Task Cleanup_OneFileLocked_OthersStillProcessed()
    {
        // File locking with FileShare.None only prevents deletion on Windows.
        // On Unix, unlink() removes the directory entry regardless of open handles.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var prDir = Path.Combine(_tempDir, "pr-7");
        Directory.CreateDirectory(prDir);
        var lockedFile = Path.Combine(prDir, "20260412-000000-000-001-locked-prompt.md");
        var deletableFile = Path.Combine(prDir, "20260412-000005-000-002-deletable-response.md");
        await File.WriteAllTextAsync(lockedFile, "locked");
        await File.WriteAllTextAsync(deletableFile, "free");

        // Hold an exclusive handle on the locked file for the duration of cleanup.
        using (var stream = File.Open(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            _ = CreateWriter();
            await WaitForAsync(() => !File.Exists(deletableFile));
        }

        Assert.True(File.Exists(lockedFile), "Locked file should remain (delete failed gracefully)");
        Assert.False(File.Exists(deletableFile), "Adjacent deletable file should still have been processed");
    }
}
