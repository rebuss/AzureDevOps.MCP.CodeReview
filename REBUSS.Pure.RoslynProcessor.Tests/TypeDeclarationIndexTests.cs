using Microsoft.CodeAnalysis.CSharp;

namespace REBUSS.Pure.RoslynProcessor.Tests;

/// <summary>
/// Focused unit tests for <see cref="TypeDeclarationIndex"/>. Verifies the
/// "top-level types only, namespace-qualified" indexing contract.
/// </summary>
public class TypeDeclarationIndexTests
{
    private static TypeDeclarationIndex IndexOf(string source) =>
        TypeDeclarationIndex.BuildFrom(CSharpSyntaxTree.ParseText(source).GetRoot());

    [Fact]
    public void BuildFrom_TopLevelClassWithoutNamespace_KeyIsBareName()
    {
        var index = IndexOf("class Foo { }");

        Assert.Single(index.Names);
        Assert.Contains("Foo", index.Names);
    }

    [Fact]
    public void BuildFrom_NamespacedTypes_AreFullyQualified()
    {
        var index = IndexOf("namespace A.B { class Foo { } class Bar { } }");

        Assert.Equal(2, index.Names.Count());
        Assert.Contains("A.B.Foo", index.Names);
        Assert.Contains("A.B.Bar", index.Names);
    }

    [Fact]
    public void BuildFrom_NestedTypes_AreExcluded()
    {
        // Nested types live inside their parent's body — they are not top-level.
        var index = IndexOf("class Outer { class Inner { } }");

        Assert.Single(index.Names);
        Assert.Contains("Outer", index.Names);
        Assert.DoesNotContain("Outer.Inner", index.Names);
    }

    [Fact]
    public void GetKindName_RendersExpectedLabels()
    {
        var rootClass = CSharpSyntaxTree.ParseText("class A { }").GetRoot();
        var rootStruct = CSharpSyntaxTree.ParseText("struct A { }").GetRoot();
        var rootRecord = CSharpSyntaxTree.ParseText("record A();").GetRoot();
        var rootRecordStruct = CSharpSyntaxTree.ParseText("record struct A();").GetRoot();
        var rootInterface = CSharpSyntaxTree.ParseText("interface A { }").GetRoot();

        Assert.Equal("class", TypeDeclarationIndex.GetKindName(
            (Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax)rootClass.DescendantNodes().First()));
        Assert.Equal("struct", TypeDeclarationIndex.GetKindName(
            (Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax)rootStruct.DescendantNodes().First()));
        Assert.Equal("record", TypeDeclarationIndex.GetKindName(
            (Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax)rootRecord.DescendantNodes().First()));
        Assert.Equal("record struct", TypeDeclarationIndex.GetKindName(
            (Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax)rootRecordStruct.DescendantNodes().First()));
        Assert.Equal("interface", TypeDeclarationIndex.GetKindName(
            (Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax)rootInterface.DescendantNodes().First()));
    }
}
