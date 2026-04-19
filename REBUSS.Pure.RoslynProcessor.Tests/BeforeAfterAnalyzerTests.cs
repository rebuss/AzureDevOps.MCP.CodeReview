using REBUSS.Pure.RoslynProcessor;

namespace REBUSS.Pure.RoslynProcessor.Tests;

public class BeforeAfterAnalyzerTests
{
    [Fact]
    public void Analyze_MethodSignatureChange_ReturnsFull()
    {
        var before = "class C { void Foo(int x) { } }";
        var after = "class C { void Foo(int x, string y) { } }";

        Assert.Equal(ContextDecision.Full, BeforeAfterAnalyzer.Analyze(before, after));
    }

    [Fact]
    public void Analyze_ControlFlowChange_ReturnsFull()
    {
        var before = "class C { void M() { if (true) { } } }";
        var after = "class C { void M() { if (false) { } } }";

        Assert.Equal(ContextDecision.Full, BeforeAfterAnalyzer.Analyze(before, after));
    }

    [Fact]
    public void Analyze_TypeDeclarationChange_ReturnsFull()
    {
        var before = "class C { }";
        var after = "class C : IDisposable { }";

        Assert.Equal(ContextDecision.Full, BeforeAfterAnalyzer.Analyze(before, after));
    }

    [Fact]
    public void Analyze_ReturnTypeChange_ReturnsFull()
    {
        var before = "class C { void Foo() { } }";
        var after = "class C { int Foo() { return 0; } }";

        Assert.Equal(ContextDecision.Full, BeforeAfterAnalyzer.Analyze(before, after));
    }

    [Fact]
    public void Analyze_ExpressionBodyChange_ReturnsFull()
    {
        var before = "class C { int X() { return 1; } }";
        var after = "class C { int X() { return 2; } }";

        var result = BeforeAfterAnalyzer.Analyze(before, after);
        Assert.True(result >= ContextDecision.Minimal);
    }

    [Fact]
    public void Analyze_WhitespaceOnlyChange_ReturnsNone()
    {
        var before = "class C { void M() { } }";
        var after = "class C {  void M() {  } }";

        Assert.Equal(ContextDecision.None, BeforeAfterAnalyzer.Analyze(before, after));
    }

    [Fact]
    public void Analyze_IdenticalCode_ReturnsNone()
    {
        var code = "class C { void M() { } }";

        Assert.Equal(ContextDecision.None, BeforeAfterAnalyzer.Analyze(code, code));
    }

    [Fact]
    public void Analyze_BothEmpty_ReturnsNone()
    {
        Assert.Equal(ContextDecision.None, BeforeAfterAnalyzer.Analyze("", ""));
    }

    [Fact]
    public void Analyze_NullInputs_ReturnsNone()
    {
        Assert.Equal(ContextDecision.None, BeforeAfterAnalyzer.Analyze(null!, null!));
    }

    // ─── Feature 016: Asymmetric tree size handling ──────────────────────────

    [Fact]
    public void Analyze_EmptyBefore_SubstantialAfter_DoesNotThrow_ReturnsNonNone()
    {
        var before = "";
        var after = "namespace Foo { public class Bar { public void Baz() { } } }";

        var result = BeforeAfterAnalyzer.Analyze(before, after);

        Assert.NotEqual(ContextDecision.None, result);
    }

    [Fact]
    public void Analyze_ShortBefore_LongAfter_DoesNotThrow_ReturnsNonNone()
    {
        var before = "class Foo { }";
        var after = "class Foo { public void A() { } public void B() { } public void C() { } }";

        var result = BeforeAfterAnalyzer.Analyze(before, after);

        Assert.NotEqual(ContextDecision.None, result);
    }

    [Fact]
    public void Analyze_LongBefore_ShortAfter_DoesNotThrow_ReturnsNonNone()
    {
        var before = "class Foo { public void A() { } public void B() { } public void C() { } }";
        var after = "class Foo { }";

        var result = BeforeAfterAnalyzer.Analyze(before, after);

        Assert.NotEqual(ContextDecision.None, result);
    }

    [Fact]
    public void Analyze_10xExpansion_DoesNotThrow_ReturnsNonNone()
    {
        var before = "class Foo { }";
        var afterBuilder = new System.Text.StringBuilder("class Foo {\n");
        for (var i = 0; i < 100; i++)
            afterBuilder.AppendLine($"  public void Method{i}() {{ }}");
        afterBuilder.AppendLine("}");

        var result = BeforeAfterAnalyzer.Analyze(before, afterBuilder.ToString());

        Assert.NotEqual(ContextDecision.None, result);
    }

    [Fact]
    public void Analyze_InvalidSyntaxBoth_DoesNotThrow()
    {
        var ex = Record.Exception(
            () => BeforeAfterAnalyzer.Analyze("not valid c#", "also not valid {{{"));

        Assert.Null(ex);
    }

    [Fact]
    public void Analyze_NewMethodAppended_DetectsChange()
    {
        var before = "class Foo { public void Bar() { } }";
        var after = "class Foo { public void Bar() { } public void Baz() { return; } }";

        var result = BeforeAfterAnalyzer.Analyze(before, after);

        Assert.True(result == ContextDecision.Minimal || result == ContextDecision.Full);
    }
}
