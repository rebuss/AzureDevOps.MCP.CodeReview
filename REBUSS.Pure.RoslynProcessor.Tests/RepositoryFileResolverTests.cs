using REBUSS.Pure.RoslynProcessor;

namespace REBUSS.Pure.RoslynProcessor.Tests;

public class RepositoryFileResolverTests : IDisposable
{
    private readonly string _tempDir;

    public RepositoryFileResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"resolver-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void ResolveRoot_NestedWrapper_ReturnsInnerDir()
    {
        var inner = Path.Combine(_tempDir, "repo-abc123");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, "file.cs"), "content");

        var root = RepositoryFileResolver.ResolveRoot(_tempDir);
        Assert.Equal(inner, root);
    }

    [Fact]
    public void ResolveRoot_FlatStructure_ReturnsOriginalPath()
    {
        File.WriteAllText(Path.Combine(_tempDir, "file.cs"), "content");
        Directory.CreateDirectory(Path.Combine(_tempDir, "src"));

        var root = RepositoryFileResolver.ResolveRoot(_tempDir);
        Assert.Equal(_tempDir, root);
    }

    // Theory inputs: forward-slash matches the format diff strings actually use (SCM APIs
    // emit POSIX paths); OS-native Path.Combine(...) pins that the resolver also tolerates
    // the host's DirectorySeparatorChar, which matters on Windows if the production-side
    // `.Replace('/', Path.DirectorySeparatorChar)` normalization is ever refactored away.
    public static TheoryData<string> SeparatorVariants() => new()
    {
        "src/File.cs",
        Path.Combine("src", "File.cs"), // "src\\File.cs" on Windows, "src/File.cs" on Unix
    };

    [Theory]
    [MemberData(nameof(SeparatorVariants))]
    public void ResolvePath_FileExists_ReturnsFullPath(string diffFilePath)
    {
        var srcDir = Path.Combine(_tempDir, "src");
        Directory.CreateDirectory(srcDir);
        var filePath = Path.Combine(srcDir, "File.cs");
        File.WriteAllText(filePath, "class C {}");

        var result = RepositoryFileResolver.ResolvePath(_tempDir, diffFilePath);
        Assert.NotNull(result);
        Assert.True(File.Exists(result));
    }

    [Theory]
    [InlineData("nonexistent/File.cs")]
    [InlineData("nonexistent\\File.cs")]
    public void ResolvePath_FileNotFound_ReturnsNull(string diffFilePath)
    {
        var result = RepositoryFileResolver.ResolvePath(_tempDir, diffFilePath);
        Assert.Null(result);
    }
}
