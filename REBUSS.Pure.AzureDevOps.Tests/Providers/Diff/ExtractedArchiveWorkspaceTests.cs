using REBUSS.Pure.AzureDevOps.Providers.Diff;

namespace REBUSS.Pure.AzureDevOps.Tests.Providers.Diff;

/// <summary>
/// Focused unit tests for <see cref="ExtractedArchiveWorkspace"/>. Static path-resolution
/// cases relocated from <c>AzureDevOpsDiffProviderTests.TryResolveFilePathTests</c>
/// (Step 3 refactor); the path-traversal defence and wrapper-fallback semantics
/// are unchanged. <see cref="ExtractedArchiveWorkspace.CreateAsync"/> is exercised
/// via the integration tests in <c>AzureDevOpsDiffProviderTests</c> (ZIP-fallback
/// heuristic) which already verify the temp-dir lifecycle end-to-end.
/// </summary>
public class ExtractedArchiveWorkspaceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"adotr-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ─── TryResolveFilePath: happy path + path-traversal defence ────────────────

    [Fact]
    public void TryResolveFilePath_NormalPath_ReturnsResolvedFile()
    {
        var rootDir = Path.Combine(_tempDir, "archive");
        var srcDir = Path.Combine(rootDir, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "File.cs"), "class C { }");

        var resolved = ExtractedArchiveWorkspace.TryResolveFilePath(rootDir, "src/File.cs");

        Assert.NotNull(resolved);
        Assert.Equal(Path.Combine(rootDir, "src", "File.cs"), resolved);
    }

    [Fact]
    public void TryResolveFilePath_ParentTraversalEscape_ReturnsNull()
    {
        // Attacker-supplied diff path escaping the archive root via `..`.
        var rootDir = Path.Combine(_tempDir, "archive");
        Directory.CreateDirectory(rootDir);

        // Plant a file OUTSIDE the root that would be reached by the traversal.
        var outsideDir = Path.Combine(_tempDir, "outside");
        Directory.CreateDirectory(outsideDir);
        File.WriteAllText(Path.Combine(outsideDir, "secret.cs"), "secret");

        // ../outside/secret.cs  ← resolves outside rootDir
        var resolved = ExtractedArchiveWorkspace.TryResolveFilePath(rootDir, "../outside/secret.cs");

        Assert.Null(resolved);
    }

    [Fact]
    public void TryResolveFilePath_ParentTraversalViaWrapper_ReturnsNull()
    {
        // Wrapper layout: archive/commit-abc/src/File.cs. A `..` traversal from inside
        // the wrapper must not reach siblings or the temp root.
        var rootDir = Path.Combine(_tempDir, "archive");
        var wrapper = Path.Combine(rootDir, "commit-abc");
        Directory.CreateDirectory(wrapper);

        // Create a sibling directory next to rootDir and a file inside it.
        var outsideDir = Path.Combine(_tempDir, "outside");
        Directory.CreateDirectory(outsideDir);
        File.WriteAllText(Path.Combine(outsideDir, "secret.cs"), "secret");

        // Without the guard, wrapper fallback + `..` segments could escape.
        var resolved = ExtractedArchiveWorkspace.TryResolveFilePath(rootDir, "../../outside/secret.cs");

        Assert.Null(resolved);
    }

    [Fact]
    public void TryResolveFilePath_LookalikeSiblingPrefix_ReturnsNull()
    {
        // rootDir = archive, a sibling named archive-evil whose full path starts with "archive"
        // would pass a naive StartsWith check that forgot the trailing separator.
        var rootDir = Path.Combine(_tempDir, "archive");
        var evilDir = Path.Combine(_tempDir, "archive-evil");
        Directory.CreateDirectory(rootDir);
        Directory.CreateDirectory(evilDir);
        File.WriteAllText(Path.Combine(evilDir, "payload.cs"), "x");

        // The normalized candidate becomes .../archive-evil/payload.cs — outside rootDir.
        var resolved = ExtractedArchiveWorkspace.TryResolveFilePath(rootDir, "../archive-evil/payload.cs");

        Assert.Null(resolved);
    }

    [Fact]
    public void TryResolveFilePath_WrapperFallback_StillResolvesLegitimatePaths()
    {
        // Azure DevOps occasionally nests the archive under a single commit-named directory.
        var rootDir = Path.Combine(_tempDir, "archive");
        var wrapper = Path.Combine(rootDir, "repo-abc123");
        var srcDir = Path.Combine(wrapper, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "File.cs"), "class C { }");

        var resolved = ExtractedArchiveWorkspace.TryResolveFilePath(rootDir, "src/File.cs");

        Assert.NotNull(resolved);
        Assert.Equal(Path.Combine(wrapper, "src", "File.cs"), resolved);
    }

    [Fact]
    public void TryResolveFilePath_RootDoesNotExist_ReturnsNull()
    {
        // Defensive check — the resolver must not throw on a missing root.
        var missing = Path.Combine(_tempDir, "does-not-exist");

        var resolved = ExtractedArchiveWorkspace.TryResolveFilePath(missing, "src/File.cs");

        Assert.Null(resolved);
    }

    [Fact]
    public void TryResolveFilePath_FileAbsentFromArchive_ReturnsNull()
    {
        // Added/deleted files miss from one side of the diff — resolver returns null,
        // not throw, so the caller can hand a `null` content to the diff builder.
        var rootDir = Path.Combine(_tempDir, "archive");
        Directory.CreateDirectory(rootDir);

        var resolved = ExtractedArchiveWorkspace.TryResolveFilePath(rootDir, "src/Missing.cs");

        Assert.Null(resolved);
    }

    [Fact]
    public void TryResolveFilePath_WrapperLayoutWithExtraTopLevelFile_FallsBackToDirectMatchOnly()
    {
        // If the archive root has a top-level file (e.g. README) alongside a single
        // directory, the wrapper-fallback heuristic must NOT trigger — it only fires
        // for "exactly one directory + zero files" layout.
        var rootDir = Path.Combine(_tempDir, "archive");
        var wrapper = Path.Combine(rootDir, "wrapper");
        Directory.CreateDirectory(wrapper);
        File.WriteAllText(Path.Combine(rootDir, "README.md"), "top-level");
        File.WriteAllText(Path.Combine(wrapper, "deep.cs"), "class D { }");

        // Without the heuristic, the wrapper file is unreachable — that's the desired behavior.
        var resolved = ExtractedArchiveWorkspace.TryResolveFilePath(rootDir, "deep.cs");

        Assert.Null(resolved);
    }

    // CreateAsync + DisposeAsync lifecycle is exercised end-to-end via the
    // ZIP-fallback heuristic integration tests in AzureDevOpsDiffProviderTests
    // (real archive bytes via System.IO.Compression.ZipArchive helpers). Reproducing
    // those here would require either making AzureDevOpsRepositoryArchiveProvider's
    // download method virtual (prod surface change) or wiring up a fake at the
    // HttpClient level — out of scope for a pure refactor; the integration coverage
    // is sufficient.
}
