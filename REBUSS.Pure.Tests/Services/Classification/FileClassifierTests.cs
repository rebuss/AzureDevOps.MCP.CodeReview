using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Shared;

namespace REBUSS.Pure.Tests.Services.Classification;

public class FileClassifierTests
{
    private readonly FileClassifier _classifier = new();

    // --- Extension ---

    [Theory]
    [InlineData("/src/Cache/CacheService.cs", ".cs")]
    [InlineData("/docs/readme.md", ".md")]
    [InlineData("/config/settings.json", ".json")]
    [InlineData("/lib/external.dll", ".dll")]
    [InlineData("Dockerfile", "")]
    [InlineData("/src/file", "")]
    public void Classify_ReturnsCorrectExtension(string path, string expectedExtension)
    {
        var result = _classifier.Classify(path);
        Assert.Equal(expectedExtension, result.Extension);
    }

    // --- IsBinary ---

    [Theory]
    [InlineData("/lib/external.dll")]
    [InlineData("/images/logo.png")]
    [InlineData("/assets/font.woff2")]
    [InlineData("/package/archive.zip")]
    [InlineData("/docs/manual.pdf")]
    public void Classify_DetectsBinaryFiles(string path)
    {
        var result = _classifier.Classify(path);
        Assert.True(result.IsBinary);
    }

    [Theory]
    [InlineData("/src/Service.cs")]
    [InlineData("/config/app.json")]
    [InlineData("/docs/readme.md")]
    public void Classify_NonBinaryFiles_AreNotBinary(string path)
    {
        var result = _classifier.Classify(path);
        Assert.False(result.IsBinary);
    }

    // --- IsGenerated ---

    [Theory]
    [InlineData("/obj/Debug/net8.0/AssemblyInfo.cs")]
    [InlineData("/bin/Release/net8.0/app.dll")]
    [InlineData("/node_modules/package/index.js")]
    [InlineData("/src/Models/Client.g.cs")]
    [InlineData("/src/Views/Main.designer.cs")]
    public void Classify_DetectsGeneratedFiles(string path)
    {
        var result = _classifier.Classify(path);
        Assert.True(result.IsGenerated);
    }

    [Theory]
    [InlineData("package-lock.json")]
    [InlineData("yarn.lock")]
    [InlineData("packages.lock.json")]
    public void Classify_DetectsGeneratedLockFiles(string path)
    {
        var result = _classifier.Classify(path);
        Assert.True(result.IsGenerated);
    }

    [Theory]
    [InlineData("/src/Service.cs")]
    [InlineData("/tests/ServiceTests.cs")]
    public void Classify_NonGeneratedFiles_AreNotGenerated(string path)
    {
        var result = _classifier.Classify(path);
        Assert.False(result.IsGenerated);
    }

    // --- IsTestFile ---

    [Theory]
    [InlineData("/tests/Cache/CacheServiceTests.cs")]
    [InlineData("/test/Utils/TestHelper.cs")]
    [InlineData("/spec/Models/UserSpec.cs")]
    [InlineData("/src/__tests__/component.test.js")]
    [InlineData("MyProject.Tests/ServiceTests.cs")]
    public void Classify_DetectsTestFiles(string path)
    {
        var result = _classifier.Classify(path);
        Assert.True(result.IsTestFile);
    }

    [Theory]
    [InlineData("/src/Cache/CacheService.cs")]
    [InlineData("/src/Utils/Helper.cs")]
    [InlineData("/docs/testing-guide.md")]
    public void Classify_NonTestFiles_AreNotTest(string path)
    {
        var result = _classifier.Classify(path);
        Assert.False(result.IsTestFile);
    }

    // --- Category ---

    [Theory]
    [InlineData("/src/Cache/CacheService.cs", FileCategory.Source)]
    [InlineData("/src/Program.cs", FileCategory.Source)]
    public void Classify_SourceFiles_HaveSourceCategory(string path, FileCategory expected)
    {
        var result = _classifier.Classify(path);
        Assert.Equal(expected, result.Category);
    }

    [Theory]
    [InlineData("/tests/CacheTests.cs", FileCategory.Test)]
    [InlineData("/test/HelperTest.cs", FileCategory.Test)]
    public void Classify_TestFiles_HaveTestCategory(string path, FileCategory expected)
    {
        var result = _classifier.Classify(path);
        Assert.Equal(expected, result.Category);
    }

    [Theory]
    [InlineData("/config/appsettings.json", FileCategory.Config)]
    [InlineData("/src/MyProject.csproj", FileCategory.Config)]
    [InlineData("/build/Directory.Build.props", FileCategory.Config)]
    public void Classify_ConfigFiles_HaveConfigCategory(string path, FileCategory expected)
    {
        var result = _classifier.Classify(path);
        Assert.Equal(expected, result.Category);
    }

    [Theory]
    [InlineData("/docs/readme.md", FileCategory.Docs)]
    [InlineData("/README.md", FileCategory.Docs)]
    [InlineData("/docs/guide.html", FileCategory.Docs)]
    [InlineData("/documentation/arch.rst", FileCategory.Docs)]
    public void Classify_DocsFiles_HaveDocsCategory(string path, FileCategory expected)
    {
        var result = _classifier.Classify(path);
        Assert.Equal(expected, result.Category);
    }

    [Fact]
    public void Classify_BinaryFile_HasBinaryCategory()
    {
        var result = _classifier.Classify("/lib/external.dll");
        Assert.Equal(FileCategory.Binary, result.Category);
    }

    [Fact]
    public void Classify_GeneratedFile_HasGeneratedCategory()
    {
        var result = _classifier.Classify("/obj/Debug/net8.0/AssemblyInfo.cs");
        Assert.Equal(FileCategory.Generated, result.Category);
    }

    // --- ReviewPriority ---

    [Theory]
    [InlineData("/src/Cache/CacheService.cs", "high")]
    [InlineData("/tests/CacheTests.cs", "medium")]
    [InlineData("/config/appsettings.json", "medium")]
    [InlineData("/docs/readme.md", "low")]
    [InlineData("/lib/external.dll", "low")]
    [InlineData("/obj/Debug/net8.0/AssemblyInfo.cs", "low")]
    public void Classify_ReturnsCorrectReviewPriority(string path, string expectedPriority)
    {
        var result = _classifier.Classify(path);
        Assert.Equal(expectedPriority, result.ReviewPriority);
    }

    // --- Category priority: binary > generated > test > config > docs > source ---

    [Fact]
    public void Classify_BinaryInTestFolder_IsBinary_NotTest()
    {
        var result = _classifier.Classify("/tests/fixtures/image.png");
        Assert.True(result.IsBinary);
        Assert.Equal(FileCategory.Binary, result.Category);
        Assert.Equal("low", result.ReviewPriority);
    }

    [Fact]
    public void Classify_GeneratedInTestFolder_IsGenerated_NotTest()
    {
        var result = _classifier.Classify("/tests/obj/Debug/net8.0/output.cs");
        Assert.True(result.IsGenerated);
        Assert.Equal(FileCategory.Generated, result.Category);
    }
}
