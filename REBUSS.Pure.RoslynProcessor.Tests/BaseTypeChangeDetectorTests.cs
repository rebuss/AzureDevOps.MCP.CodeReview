using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace REBUSS.Pure.RoslynProcessor.Tests;

/// <summary>
/// Focused unit tests for <see cref="BaseTypeChangeDetector.Detect"/>. Each case
/// targets one branch of the 3-way decision (added+removed → "Base type changed";
/// added → "Implements added"; removed → "Implements removed"; identical → no events).
/// </summary>
public class BaseTypeChangeDetectorTests
{
    private static TypeDeclarationSyntax FirstType(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        return root.DescendantNodes().OfType<TypeDeclarationSyntax>().First();
    }

    [Fact]
    public void Detect_NoBaseListChange_EmitsNothing()
    {
        var before = FirstType("class C : IBar { }");
        var after = FirstType("class C : IBar { }");

        var changes = BaseTypeChangeDetector.Detect(before, after).ToList();

        Assert.Empty(changes);
    }

    [Fact]
    public void Detect_OnlyAdded_EmitsImplementsAdded()
    {
        var before = FirstType("class C { }");
        var after = FirstType("class C : IBar, IBaz { }");

        var change = Assert.Single(BaseTypeChangeDetector.Detect(before, after));

        Assert.Equal(StructuralChangeKind.BaseTypeChanged, change.Kind);
        Assert.Equal("Implements added: IBar, IBaz", change.Description);
    }

    [Fact]
    public void Detect_OnlyRemoved_EmitsImplementsRemoved()
    {
        var before = FirstType("class C : IBar, IBaz { }");
        var after = FirstType("class C { }");

        var change = Assert.Single(BaseTypeChangeDetector.Detect(before, after));

        Assert.Equal(StructuralChangeKind.BaseTypeChanged, change.Kind);
        Assert.Equal("Implements removed: IBar, IBaz", change.Description);
    }

    [Fact]
    public void Detect_AddedAndRemoved_EmitsBaseTypeChangedWithArrow()
    {
        var before = FirstType("class C : Base1, IFoo { }");
        var after = FirstType("class C : Base2, IFoo { }");

        var change = Assert.Single(BaseTypeChangeDetector.Detect(before, after));

        Assert.Equal(StructuralChangeKind.BaseTypeChanged, change.Kind);
        Assert.Equal("Base type changed: Base1 → Base2", change.Description);
    }
}
