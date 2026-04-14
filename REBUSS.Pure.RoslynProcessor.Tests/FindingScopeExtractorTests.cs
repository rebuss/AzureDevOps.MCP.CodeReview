using REBUSS.Pure.RoslynProcessor;

namespace REBUSS.Pure.RoslynProcessor.Tests;

/// <summary>
/// Tests for <see cref="FindingScopeExtractor"/>. Feature 021.
/// </summary>
public class FindingScopeExtractorTests
{
    [Fact]
    public void ExtractScopeBody_MethodLine_ReturnsMethodBody()
    {
        var code = """
            namespace Test;
            public class Foo
            {
                public int Bar(int x)
                {
                    var y = x + 1;
                    return y;
                }
            }
            """;
        // Line 6 = "var y = x + 1;"
        var (body, name, resolved) = FindingScopeExtractor.ExtractScopeBody(code, 6, 100);

        Assert.True(resolved);
        Assert.Contains("public int Bar", body);
        Assert.Contains("var y = x + 1;", body);
        Assert.Contains("Bar", name);
    }

    [Fact]
    public void ExtractScopeBody_ConstructorLine_ReturnsConstructor()
    {
        var code = """
            namespace Test;
            public class Foo
            {
                public Foo(int x)
                {
                    Value = x;
                }
                public int Value { get; }
            }
            """;
        // Line 6 = "Value = x;"
        var (body, name, resolved) = FindingScopeExtractor.ExtractScopeBody(code, 6, 100);

        Assert.True(resolved);
        Assert.Contains("public Foo(int x)", body);
        Assert.Contains("ctor", name);
    }

    [Fact]
    public void ExtractScopeBody_PropertyLine_ReturnsProperty()
    {
        var code = """
            namespace Test;
            public class Foo
            {
                public int Value { get; set; }
            }
            """;
        // Line 4 = property declaration
        var (_, name, resolved) = FindingScopeExtractor.ExtractScopeBody(code, 4, 100);

        Assert.True(resolved);
        Assert.Contains("Value", name);
    }

    [Fact]
    public void ExtractScopeBody_ClassLevelField_ReturnsEnclosingClass()
    {
        // A class-level field has the enclosing type as its "scope" — the field line
        // resolves to the class declaration body (FindEnclosingMember walks up to
        // TypeDeclarationSyntax when no member matches).
        var code = """
            namespace Test;
            public class Foo
            {
                private readonly int _x = 0;
                public int Bar() => _x;
            }
            """;
        // Line 4 = field declaration
        var (body, name, resolved) = FindingScopeExtractor.ExtractScopeBody(code, 4, 100);

        Assert.True(resolved);
        // Scope name should be the class Foo since the field has no enclosing member.
        Assert.Equal("Foo", name);
        Assert.Contains("class Foo", body);
    }

    [Fact]
    public void ExtractScopeBody_EmptySource_ReturnsNotResolved()
    {
        var (body, name, resolved) = FindingScopeExtractor.ExtractScopeBody("", 1, 100);

        Assert.False(resolved);
        Assert.Equal("", body);
        Assert.Equal("", name);
    }

    [Fact]
    public void ExtractScopeBody_LineOutOfRange_ReturnsNotResolved()
    {
        var code = "namespace Test;\npublic class Foo { }\n";
        var (_, _, resolved) = FindingScopeExtractor.ExtractScopeBody(code, 100, 100);

        Assert.False(resolved);
    }

    [Fact]
    public void ExtractScopeBody_LargeMethodBody_TruncatesAroundLine()
    {
        // Build a method with ~200 lines of body.
        var lines = new List<string>
        {
            "namespace Test;",
            "public class Foo",
            "{",
            "    public void Bar()",
            "    {",
        };
        for (var i = 0; i < 200; i++)
            lines.Add($"        var x{i} = {i};");
        lines.Add("    }");
        lines.Add("}");
        var code = string.Join("\n", lines);

        // Finding at line 100 (middle of the method). maxLines=50.
        var (body, _, resolved) = FindingScopeExtractor.ExtractScopeBody(code, 100, 50);

        Assert.True(resolved);
        var bodyLines = body.Split('\n');
        // Should be roughly bounded: 50 actual lines + possibly 2 truncation markers.
        Assert.True(bodyLines.Length <= 55, $"Expected <=55 lines, got {bodyLines.Length}");
        // Should contain the centered finding's line content.
        Assert.Contains("x95", body);
        // Should contain an omission marker since the body was larger than 50 lines.
        Assert.Contains("lines omitted", body);
    }

    [Fact]
    public void ExtractScopeBody_BodyUnderLimit_NoTruncationMarker()
    {
        var code = """
            namespace Test;
            public class Foo
            {
                public void Bar()
                {
                    var x = 1;
                    var y = 2;
                    var z = x + y;
                }
            }
            """;
        var (body, _, resolved) = FindingScopeExtractor.ExtractScopeBody(code, 6, 100);

        Assert.True(resolved);
        Assert.DoesNotContain("lines omitted", body);
    }

    [Fact]
    public void ExtractScopeBody_ZeroMaxLines_ReturnsNotResolved()
    {
        var code = "namespace Test;\npublic class Foo { public void Bar() { } }";
        var (_, _, resolved) = FindingScopeExtractor.ExtractScopeBody(code, 1, 0);

        Assert.False(resolved);
    }

    [Fact]
    public void ExtractScopeBody_LineInLocalFunction_ReturnsLocalFunction()
    {
        var code = """
            namespace Test;
            public class Foo
            {
                public void Bar()
                {
                    int Inner(int x) { return x * 2; }
                    var r = Inner(3);
                }
            }
            """;
        // Line 6 = local function declaration. Inner is a LocalFunctionStatementSyntax,
        // a member kind handled by FindEnclosingMember.
        var (_, name, resolved) = FindingScopeExtractor.ExtractScopeBody(code, 6, 100);

        Assert.True(resolved);
        Assert.Contains("Inner", name);
    }
}
