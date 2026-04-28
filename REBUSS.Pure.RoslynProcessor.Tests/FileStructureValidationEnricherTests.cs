using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using REBUSS.Pure.Core;

namespace REBUSS.Pure.RoslynProcessor.Tests;

public class FileStructureValidationEnricherTests : IDisposable
{
    private readonly IRepositoryDownloadOrchestrator _orchestrator = Substitute.For<IRepositoryDownloadOrchestrator>();
    private readonly DiffSourceResolver _sourceResolver;
    private readonly FileStructureValidationEnricher _enricher;
    private readonly string _tempDir;

    public FileStructureValidationEnricherTests()
    {
        _sourceResolver = new DiffSourceResolver(
            _orchestrator,
            NullLogger<DiffSourceResolver>.Instance);
        _enricher = new FileStructureValidationEnricher(
            _sourceResolver,
            NullLogger<FileStructureValidationEnricher>.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), $"file-structure-test-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public void Order_Is400()
    {
        Assert.Equal(400, _enricher.Order);
    }

    [Fact]
    public void CanEnrich_CsFile_ReturnsTrue()
    {
        Assert.True(_enricher.CanEnrich("=== src/File.cs (edit: +1 -1) ===\n@@"));
    }

    [Fact]
    public void CanEnrich_NonCsFile_ReturnsFalse()
    {
        Assert.False(_enricher.CanEnrich("=== src/main.py (edit: +1 -1) ===\n@@"));
    }

    [Fact]
    public void CanEnrich_SkippedFile_ReturnsFalse()
    {
        Assert.False(_enricher.CanEnrich("=== src/File.cs (edit: skipped) ===\n"));
    }

    [Fact]
    public void Validate_ValidCode_ReturnsAllTrue()
    {
        var code = """
            namespace Test;
            public class Foo
            {
                public void Bar()
                {
                    var x = 1;
                }
            }
            """;

        var (syntaxValid, balancedBraces) = FileStructureValidationEnricher.Validate(code);

        Assert.True(syntaxValid);
        Assert.True(balancedBraces);
    }

    [Fact]
    public void Validate_MissingClosingBrace_BracesUnbalanced()
    {
        var code = """
            namespace Test;
            public class Foo
            {
                public void Bar()
                {
                    var x = 1;
                }
            """;

        var (_, balancedBraces) = FileStructureValidationEnricher.Validate(code);

        Assert.False(balancedBraces);
    }

    [Fact]
    public void Validate_BracesInStringLiterals_DoNotAffectBalance()
    {
        var code = """
            namespace Test;
            public class Foo
            {
                public string Bar() => "{ unbalanced";
            }
            """;

        var (syntaxValid, balancedBraces) = FileStructureValidationEnricher.Validate(code);

        Assert.True(syntaxValid);
        Assert.True(balancedBraces);
    }

    [Fact]
    public void FormatAnnotation_BothTrue_FormatsCorrectly()
    {
        var annotation = FileStructureValidationEnricher.FormatAnnotation(true, true);
        Assert.Equal("[file-structure: compiles=yes, balanced-braces=yes]\n", annotation);
    }

    [Fact]
    public void FormatAnnotation_BothFalse_FormatsCorrectly()
    {
        var annotation = FileStructureValidationEnricher.FormatAnnotation(false, false);
        Assert.Equal("[file-structure: compiles=no, balanced-braces=no]\n", annotation);
    }

    [Fact]
    public void FormatAnnotation_MixedValues_FormatsCorrectly()
    {
        var annotation = FileStructureValidationEnricher.FormatAnnotation(true, false);
        Assert.Equal("[file-structure: compiles=yes, balanced-braces=no]\n", annotation);
    }

    [Fact]
    public void FormatAnnotation_BothNull_EmitsUnknown()
    {
        // Feature 021: nullable inputs produce `unknown` status. Callers emit this
        // when source is unavailable so the annotation is still present — the
        // reviewer prompt treats `unknown` as "assume structurally valid".
        var annotation = FileStructureValidationEnricher.FormatAnnotation(null, null);
        Assert.Equal("[file-structure: compiles=unknown, balanced-braces=unknown]\n", annotation);
    }

    [Fact]
    public void FormatAnnotation_MixedNullAndBool_FormatsCorrectly()
    {
        var annotation = FileStructureValidationEnricher.FormatAnnotation(true, null);
        Assert.Equal("[file-structure: compiles=yes, balanced-braces=unknown]\n", annotation);
    }

    [Fact]
    public async Task EnrichAsync_ValidFile_InsertsAnnotationAfterHeader()
    {
        var wrapperDir = Path.Combine(_tempDir, "repo");
        var srcDir = Path.Combine(wrapperDir, "src");
        Directory.CreateDirectory(srcDir);

        var afterCode = """
            namespace Test;
            public class OrderService
            {
                public void ProcessOrder()
                {
                    var x = 1;
                }
            }
            """;
        File.WriteAllText(Path.Combine(srcDir, "OrderService.cs"), afterCode);
        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns(_tempDir);

        var diff = "=== src/OrderService.cs (edit: +1 -1) ===\n@@ -5,1 +5,1 @@\n-        var x = 0;\n+        var x = 1;";
        var result = await _enricher.EnrichAsync(diff);

        Assert.Contains("[file-structure: compiles=yes, balanced-braces=yes]", result);
        // Annotation should appear between header and first hunk
        var headerEnd = result.IndexOf("===\n", StringComparison.Ordinal) + 4;
        var annotationStart = result.IndexOf("[file-structure:", StringComparison.Ordinal);
        var hunkStart = result.IndexOf("@@ ", StringComparison.Ordinal);
        Assert.True(annotationStart >= headerEnd, "Annotation should be after header");
        Assert.True(annotationStart < hunkStart, "Annotation should be before first hunk");
    }

    [Fact]
    public async Task EnrichAsync_ZeroHunks_ReturnsDiffUnchanged()
    {
        var diff = "=== src/File.cs (renamed, no content change) ===\n";
        var result = await _enricher.EnrichAsync(diff);
        Assert.Equal(diff, result);
    }

    [Fact]
    public async Task EnrichAsync_ResolverReturnsNull_EmitsUnknownAnnotation()
    {
        // Feature 021: when source is unavailable, the enricher must still emit an
        // annotation (with `unknown` values) rather than returning the diff unchanged.
        // Otherwise, a missing annotation is interpreted by the reviewer as "file may
        // be broken", producing structural false positives.
        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns((string?)null);

        var diff = "=== src/Missing.cs (edit: +1 -1) ===\n@@ -1,1 +1,1 @@\n-old\n+new";
        var result = await _enricher.EnrichAsync(diff);

        Assert.NotEqual(diff, result);
        Assert.Contains("[file-structure: compiles=unknown, balanced-braces=unknown]", result);
    }
}
