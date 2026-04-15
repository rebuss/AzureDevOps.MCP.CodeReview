using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using REBUSS.Pure.Core;
using REBUSS.Pure.RoslynProcessor;
using REBUSS.Pure.Services.CopilotReview.Validation;

namespace REBUSS.Pure.Tests.Services.CopilotReview.Validation;

/// <summary>Unit tests for <see cref="FindingScopeResolver"/>. Feature 021.</summary>
public class FindingScopeResolverTests : IDisposable
{
    private readonly IRepositoryDownloadOrchestrator _orchestrator = Substitute.For<IRepositoryDownloadOrchestrator>();
    private readonly DiffSourceResolver _sourceResolver;
    private readonly FindingScopeResolver _resolver;
    private readonly string _tempDir;

    public FindingScopeResolverTests()
    {
        _sourceResolver = new DiffSourceResolver(_orchestrator, NullLogger<DiffSourceResolver>.Instance);
        _resolver = new FindingScopeResolver(_sourceResolver, NullLogger<FindingScopeResolver>.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), $"finding-scope-resolver-test-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    private static ParsedFinding MakeFinding(string filePath, int? line = null) => new()
    {
        Index = 0,
        FilePath = filePath,
        LineNumber = line,
        Severity = "major",
        Description = "test",
        OriginalText = "test",
    };

    [Fact]
    public async Task ResolveAsync_EmptyInput_ReturnsEmpty()
    {
        var result = await _resolver.ResolveAsync(
            Array.Empty<ParsedFinding>(), 150, CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task ResolveAsync_NonCSharpFile_ReturnsNotCSharp()
    {
        var findings = new[]
        {
            MakeFinding("src/config.json", 5),
            MakeFinding("docs/README.md", 10),
            MakeFinding("src/app.ts", 15),
        };

        var result = await _resolver.ResolveAsync(findings, 150, CancellationToken.None);

        Assert.All(result, r => Assert.Equal(ScopeResolutionFailure.NotCSharp, r.ResolutionFailure));
        Assert.All(result, r => Assert.Equal("", r.ScopeSource));
    }

    [Fact]
    public async Task ResolveAsync_SourceUnavailable_ReturnsSourceUnavailable()
    {
        // orchestrator returns null => DiffSourceResolver returns null pair => SourceUnavailable.
        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns((string?)null);

        var findings = new[] { MakeFinding("src/Missing.cs", 5) };
        var result = await _resolver.ResolveAsync(findings, 150, CancellationToken.None);

        var r = Assert.Single(result);
        Assert.Equal(ScopeResolutionFailure.SourceUnavailable, r.ResolutionFailure);
    }

    [Fact]
    public async Task ResolveAsync_NoLineAndNoIdentifiers_FallsBackToWholeFile()
    {
        // When Copilot gives neither a line number nor any backtick identifier that
        // FindingLineResolver can match, the resolver should still send the file to
        // validation using the whole-file fallback — ResolutionFailure.None, scope
        // name marked as an entire-file anchor.
        SetupFileSource("Foo.cs", """
            namespace Test;
            public class Foo
            {
                public void Bar() { }
            }
            """);

        var findings = new[] { MakeFinding("Foo.cs", line: null) };

        var result = await _resolver.ResolveAsync(findings, 150, CancellationToken.None);

        var r = Assert.Single(result);
        Assert.Equal(ScopeResolutionFailure.None, r.ResolutionFailure);
        Assert.Contains("entire file", r.ScopeName);
        Assert.Contains("Bar", r.ScopeSource);
    }

    [Fact]
    public async Task ResolveAsync_NoLineButIdentifierInDescription_RecoversLineViaRoslyn()
    {
        // Copilot omitted the line number but mentioned `Bar` in backticks — the
        // FindingLineResolver should walk the syntax tree, find the method
        // declaration, and feed its line into the scope extractor. Result: proper
        // method-level scope, not whole-file fallback.
        SetupFileSource("Foo.cs", """
            namespace Test;
            public class Foo
            {
                public void Bar()
                {
                    var x = 1;
                }
            }
            """);

        var finding = new ParsedFinding
        {
            Index = 0,
            FilePath = "Foo.cs",
            LineNumber = null,
            Severity = "major",
            Description = "`Bar` leaks something",
            OriginalText = "...",
        };

        var result = await _resolver.ResolveAsync(new[] { finding }, 150, CancellationToken.None);

        var r = Assert.Single(result);
        Assert.Equal(ScopeResolutionFailure.None, r.ResolutionFailure);
        Assert.Contains("Bar", r.ScopeName);
        Assert.DoesNotContain("entire file", r.ScopeName);
    }

    [Fact]
    public async Task ResolveAsync_LineOutsideAnyMember_FallsBackViaIdentifier()
    {
        // When Copilot's line points somewhere Roslyn cannot find an enclosing
        // member (e.g. a `using` or `namespace` line), but the description cites
        // an identifier, the resolver retries with the hint and recovers the
        // correct scope.
        SetupFileSource("Foo.cs", """
            using System;

            namespace Test;

            public class Foo
            {
                public void Bar()
                {
                    var x = 1;
                }
            }
            """);

        var finding = new ParsedFinding
        {
            Index = 0,
            FilePath = "Foo.cs",
            LineNumber = 1,                     // "using System;" — not inside any member
            Severity = "major",
            Description = "`Bar` has an issue",
            OriginalText = "...",
        };

        var result = await _resolver.ResolveAsync(new[] { finding }, 150, CancellationToken.None);

        var r = Assert.Single(result);
        Assert.Equal(ScopeResolutionFailure.None, r.ResolutionFailure);
        Assert.Contains("Bar", r.ScopeName);
        Assert.DoesNotContain("entire file", r.ScopeName);
    }

    [Fact]
    public async Task ResolveAsync_ValidCSharpFile_ReturnsNoneWithScope()
    {
        SetupFileSource("Foo.cs", """
            namespace Test;
            public class Foo
            {
                public void Bar()
                {
                    var x = 1;
                }
            }
            """);

        // Line 6 = "var x = 1;" inside Bar().
        var findings = new[] { MakeFinding("Foo.cs", line: 6) };

        var result = await _resolver.ResolveAsync(findings, 150, CancellationToken.None);

        var r = Assert.Single(result);
        Assert.Equal(ScopeResolutionFailure.None, r.ResolutionFailure);
        Assert.Contains("Bar", r.ScopeName);
        Assert.Contains("var x = 1;", r.ScopeSource);
    }

    [Fact]
    public async Task ResolveAsync_GroupsByFilePath_SingleSourceResolutionPerFile()
    {
        SetupFileSource("Foo.cs", """
            namespace Test;
            public class Foo
            {
                public void A() { }
                public void B() { }
                public void C() { }
            }
            """);

        var findings = new[]
        {
            MakeFinding("Foo.cs", line: 4),
            MakeFinding("Foo.cs", line: 5),
            MakeFinding("Foo.cs", line: 6),
        };

        var result = await _resolver.ResolveAsync(findings, 150, CancellationToken.None);

        Assert.Equal(3, result.Count);
        Assert.All(result, r => Assert.Equal(ScopeResolutionFailure.None, r.ResolutionFailure));
        // Each finding resolves to its own enclosing method.
        var names = result.Select(r => r.ScopeName).ToHashSet();
        Assert.Equal(3, names.Count);
    }

    [Fact]
    public async Task ResolveAsync_MixedFailures_EachMappedCorrectly()
    {
        SetupFileSource("Foo.cs", """
            namespace Test;
            public class Foo
            {
                public void Bar() { var x = 1; }
            }
            """);

        var findings = new[]
        {
            MakeFinding("config.json", line: 5),            // NotCSharp
            MakeFinding("Foo.cs", line: 4),                 // None (method body line)
            MakeFinding("Foo.cs", line: null),              // None (whole-file fallback)
        };

        var result = await _resolver.ResolveAsync(findings, 150, CancellationToken.None);

        Assert.Equal(ScopeResolutionFailure.NotCSharp, result[0].ResolutionFailure);
        Assert.Equal(ScopeResolutionFailure.None, result[1].ResolutionFailure);
        // Third finding has no line and no backtick identifier in description →
        // whole-file fallback (still ResolutionFailure.None so it reaches Copilot).
        Assert.Equal(ScopeResolutionFailure.None, result[2].ResolutionFailure);
        Assert.Contains("entire file", result[2].ScopeName);
    }

    private void SetupFileSource(string relativePath, string content)
    {
        var wrapperDir = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(wrapperDir);
        var fullPath = Path.Combine(wrapperDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        _orchestrator.GetExtractedPathAsync(Arg.Any<CancellationToken>()).Returns(_tempDir);
    }
}
