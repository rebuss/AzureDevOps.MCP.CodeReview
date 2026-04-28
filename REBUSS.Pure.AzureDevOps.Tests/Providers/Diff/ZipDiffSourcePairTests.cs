using Microsoft.Extensions.Logging.Abstractions;
using REBUSS.Pure.AzureDevOps.Providers.Diff;

namespace REBUSS.Pure.AzureDevOps.Tests.Providers.Diff;

/// <summary>
/// Focused unit tests for <see cref="ZipDiffSourcePair"/>. Uses real temp directories
/// constructed via the <c>internal</c> <see cref="ExtractedArchiveWorkspace"/> ctor —
/// the network-bound <see cref="ExtractedArchiveWorkspace.CreateAsync"/> path is
/// covered separately by the ZIP-fallback integration tests in
/// <c>AzureDevOpsDiffProviderTests</c>.
/// </summary>
public class ZipDiffSourcePairTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"zdsp-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private (ExtractedArchiveWorkspace workspace, string baseDir, string targetDir) BuildWorkspace()
    {
        var instanceDir = Path.Combine(_tempDir, "diff-instance");
        var baseDir = Path.Combine(instanceDir, "base");
        var targetDir = Path.Combine(instanceDir, "target");
        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(targetDir);
        return (new ExtractedArchiveWorkspace(instanceDir, baseDir, targetDir, NullLogger.Instance),
            baseDir, targetDir);
    }

    [Fact]
    public async Task ReadAsync_ReturnsBothContents_WhenFileExistsInBaseAndTarget()
    {
        var (workspace, baseDir, targetDir) = BuildWorkspace();
        var srcRel = Path.Combine("src", "File.cs");
        Directory.CreateDirectory(Path.Combine(baseDir, "src"));
        Directory.CreateDirectory(Path.Combine(targetDir, "src"));
        await File.WriteAllTextAsync(Path.Combine(baseDir, srcRel), "base content");
        await File.WriteAllTextAsync(Path.Combine(targetDir, srcRel), "target content");

        await using var pair = new ZipDiffSourcePair(workspace);
        var (baseContent, targetContent) = await pair.ReadAsync("src/File.cs", CancellationToken.None);

        Assert.Equal("base content", baseContent);
        Assert.Equal("target content", targetContent);
    }

    [Fact]
    public async Task ReadAsync_AddedFile_BaseContentIsNull_TargetContentReturned()
    {
        // Added file: missing from base archive, present in target.
        var (workspace, baseDir, targetDir) = BuildWorkspace();
        Directory.CreateDirectory(Path.Combine(targetDir, "src"));
        await File.WriteAllTextAsync(Path.Combine(targetDir, "src", "Added.cs"), "new file");

        await using var pair = new ZipDiffSourcePair(workspace);
        var (baseContent, targetContent) = await pair.ReadAsync("src/Added.cs", CancellationToken.None);

        Assert.Null(baseContent);
        Assert.Equal("new file", targetContent);
    }

    [Fact]
    public async Task ReadAsync_DeletedFile_TargetContentIsNull_BaseContentReturned()
    {
        // Deleted file: present in base, missing from target.
        var (workspace, baseDir, targetDir) = BuildWorkspace();
        Directory.CreateDirectory(Path.Combine(baseDir, "src"));
        await File.WriteAllTextAsync(Path.Combine(baseDir, "src", "Removed.cs"), "old content");

        await using var pair = new ZipDiffSourcePair(workspace);
        var (baseContent, targetContent) = await pair.ReadAsync("src/Removed.cs", CancellationToken.None);

        Assert.Equal("old content", baseContent);
        Assert.Null(targetContent);
    }

    [Fact]
    public async Task ReadAsync_LeadingSlashInPath_ResolvedCorrectly()
    {
        // ADO file paths come in with a leading slash; TryResolveFilePath strips it.
        var (workspace, baseDir, targetDir) = BuildWorkspace();
        Directory.CreateDirectory(Path.Combine(baseDir, "src"));
        Directory.CreateDirectory(Path.Combine(targetDir, "src"));
        await File.WriteAllTextAsync(Path.Combine(baseDir, "src", "F.cs"), "b");
        await File.WriteAllTextAsync(Path.Combine(targetDir, "src", "F.cs"), "t");

        await using var pair = new ZipDiffSourcePair(workspace);
        var (baseContent, targetContent) = await pair.ReadAsync("/src/F.cs", CancellationToken.None);

        Assert.Equal("b", baseContent);
        Assert.Equal("t", targetContent);
    }

    [Fact]
    public async Task DisposeAsync_DelegatesToWorkspace_RemovingTempDirectory()
    {
        var (workspace, baseDir, _) = BuildWorkspace();
        Assert.True(Directory.Exists(baseDir));

        var pair = new ZipDiffSourcePair(workspace);
        await pair.DisposeAsync();

        Assert.False(Directory.Exists(baseDir));
    }
}
