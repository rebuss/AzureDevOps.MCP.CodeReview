using REBUSS.Pure.RoslynProcessor;

namespace REBUSS.Pure.RoslynProcessor.Tests;

public class FindingLineResolverTests
{
    private const string Source = """
        namespace Test;

        public class Foo
        {
            private int _field;

            public void Bar()
            {
                var x = 1;
            }

            public void Baz(int n)
            {
                var y = n;
            }

            public int Prop { get; set; }

            public void Bar(string s)
            {
                // overload
            }
        }
        """;

    [Fact]
    public void TryResolveLine_NoIdentifiersInDescription_ReturnsNull()
    {
        var line = FindingLineResolver.TryResolveLine(Source, "some free-form text with no backticks", null);
        Assert.Null(line);
    }

    [Fact]
    public void TryResolveLine_MethodInBackticks_ReturnsMethodLine()
    {
        var line = FindingLineResolver.TryResolveLine(Source, "`Bar` leaks", null);
        Assert.NotNull(line);
        // First `Bar()` declaration starts at line 7 (1-based) in the source above.
        Assert.Equal(7, line);
    }

    [Fact]
    public void TryResolveLine_PropertyInBackticks_ReturnsPropertyLine()
    {
        var line = FindingLineResolver.TryResolveLine(Source, "`Prop` is not initialized", null);
        Assert.NotNull(line);
        // `Prop` sits at its own line inside the class.
        Assert.True(line > 7);
    }

    [Fact]
    public void TryResolveLine_FieldInBackticks_ReturnsFieldLine()
    {
        var line = FindingLineResolver.TryResolveLine(Source, "`_field` leaks", null);
        Assert.NotNull(line);
    }

    [Fact]
    public void TryResolveLine_IdentifierWithQualifier_StripsLeadingSegments()
    {
        // `Foo.Bar` — resolver should match on the trailing segment (Bar).
        var line = FindingLineResolver.TryResolveLine(Source, "`Foo.Bar` is buggy", null);
        Assert.NotNull(line);
    }

    [Fact]
    public void TryResolveLine_OverloadedMethod_HintLineSelectsNearest()
    {
        // Two `Bar` declarations exist — one at ~line 7, one near the bottom.
        // Hint close to the second one must pick the later one, not the first.
        var withoutHint = FindingLineResolver.TryResolveLine(Source, "`Bar` leaks", null);
        var withLateHint = FindingLineResolver.TryResolveLine(Source, "`Bar` leaks", hintLine: 19);

        Assert.NotNull(withoutHint);
        Assert.NotNull(withLateHint);
        Assert.NotEqual(withoutHint, withLateHint);
        Assert.True(withLateHint > withoutHint);
    }

    [Fact]
    public void TryResolveLine_EmptySource_ReturnsNull()
    {
        Assert.Null(FindingLineResolver.TryResolveLine("", "`Bar` leaks", null));
    }

    [Fact]
    public void TryResolveLine_EmptyDescription_ReturnsNull()
    {
        Assert.Null(FindingLineResolver.TryResolveLine(Source, "", null));
    }

    [Fact]
    public void TryResolveLine_UnparseableSource_ReturnsNull()
    {
        // Roslyn parses leniently so almost anything succeeds — use a string that has
        // no identifiers Roslyn would recognize as declarations.
        var line = FindingLineResolver.TryResolveLine("///", "`Bar` leaks", null);
        Assert.Null(line);
    }
}
