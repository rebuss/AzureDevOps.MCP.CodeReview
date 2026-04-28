using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using REBUSS.Pure.RoslynProcessor.Members;

namespace REBUSS.Pure.RoslynProcessor.Tests.Members;

/// <summary>
/// Focused unit tests for <see cref="MemberKeyExtractor"/>. Verifies that
/// <see cref="MemberKeyExtractor.Key"/> distinguishes overloads by parameter types
/// while <see cref="MemberKeyExtractor.GroupKey"/> collapses them to name-and-kind
/// (the orphan-pairing heuristic depends on this distinction).
/// </summary>
public class MemberKeyExtractorTests
{
    private static MemberDeclarationSyntax FirstMember(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        return root.DescendantNodes().OfType<TypeDeclarationSyntax>().First().Members.First();
    }

    [Fact]
    public void Key_Method_DistinguishesOverloadsByParamTypes()
    {
        var foo1 = FirstMember("class C { void Foo(int x) { } }");
        var foo2 = FirstMember("class C { void Foo(string x) { } }");

        Assert.Equal("method:Foo(int)", MemberKeyExtractor.Key(foo1));
        Assert.Equal("method:Foo(string)", MemberKeyExtractor.Key(foo2));
        Assert.NotEqual(MemberKeyExtractor.Key(foo1), MemberKeyExtractor.Key(foo2));
    }

    [Fact]
    public void Key_Indexer_IncludesParamTypesAsThisBracket()
    {
        var indexer = FirstMember("class C { public int this[int i] { get => 0; } }");
        Assert.Equal("property:this[int]", MemberKeyExtractor.Key(indexer));
    }

    [Fact]
    public void Key_Constructor_UsesCtorMarker()
    {
        var ctor = FirstMember("class C { public C(int x) { } }");
        Assert.Equal("method:.ctor(int)", MemberKeyExtractor.Key(ctor));
    }

    [Fact]
    public void Key_Field_TakesFirstVariableName()
    {
        // Multi-variable field declarations are keyed by the first variable.
        var field = FirstMember("class C { int a, b, c; }");
        Assert.Equal("field:a", MemberKeyExtractor.Key(field));
    }

    [Fact]
    public void Key_Operator_IncludesOperatorTokenAndParamTypes()
    {
        var op = FirstMember("class C { public static C operator +(C a, C b) => null; }");
        Assert.Equal("method:operator +(C,C)", MemberKeyExtractor.Key(op));
    }

    [Fact]
    public void GroupKey_Method_CollapsesOverloadsToNameOnly()
    {
        var foo1 = FirstMember("class C { void Foo(int x) { } }");
        var foo2 = FirstMember("class C { void Foo(string x, double y) { } }");

        Assert.Equal("method:Foo", MemberKeyExtractor.GroupKey(foo1));
        Assert.Equal("method:Foo", MemberKeyExtractor.GroupKey(foo2));
    }

    [Fact]
    public void GroupKey_Indexer_CollapsesAcrossParamCounts()
    {
        var oneParam = FirstMember("class C { public int this[int i] { get => 0; } }");
        var twoParams = FirstMember("class C { public int this[int i, int j] { get => 0; } }");

        Assert.Equal("property:this[]", MemberKeyExtractor.GroupKey(oneParam));
        Assert.Equal("property:this[]", MemberKeyExtractor.GroupKey(twoParams));
    }
}
