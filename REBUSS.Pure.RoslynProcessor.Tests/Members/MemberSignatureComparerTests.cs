using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using REBUSS.Pure.RoslynProcessor.Members;

namespace REBUSS.Pure.RoslynProcessor.Tests.Members;

/// <summary>
/// Focused unit tests for <see cref="MemberSignatureComparer.AreEqual"/>. Each case
/// targets one signature axis (params, modifiers, type params, accessor kinds, type)
/// to confirm the per-kind switch dispatches correctly.
/// </summary>
public class MemberSignatureComparerTests
{
    private static MemberDeclarationSyntax FirstMember(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        return root.DescendantNodes().OfType<TypeDeclarationSyntax>().First().Members.First();
    }

    [Fact]
    public void AreEqual_Method_ParamDifference_ReturnsFalse()
    {
        var a = FirstMember("class C { void Foo(int x) { } }");
        var b = FirstMember("class C { void Foo(string x) { } }");
        Assert.False(MemberSignatureComparer.AreEqual(a, b));
    }

    [Fact]
    public void AreEqual_Method_ModifierDifference_ReturnsFalse()
    {
        var a = FirstMember("class C { void Foo() { } }");
        var b = FirstMember("class C { static void Foo() { } }");
        Assert.False(MemberSignatureComparer.AreEqual(a, b));
    }

    [Fact]
    public void AreEqual_Method_TypeParameterDifference_ReturnsFalse()
    {
        var a = FirstMember("class C { void Foo<T>(T x) { } }");
        var b = FirstMember("class C { void Foo<T, U>(T x) { } }");
        Assert.False(MemberSignatureComparer.AreEqual(a, b));
    }

    [Fact]
    public void AreEqual_Method_ReturnTypeDifference_ReturnsFalse()
    {
        var a = FirstMember("class C { int Foo() { return 0; } }");
        var b = FirstMember("class C { string Foo() { return null; } }");
        Assert.False(MemberSignatureComparer.AreEqual(a, b));
    }

    [Fact]
    public void AreEqual_Property_AccessorKindsDifference_ReturnsFalse()
    {
        var a = FirstMember("class C { public int X { get; set; } }");
        var b = FirstMember("class C { public int X { get; } }");
        Assert.False(MemberSignatureComparer.AreEqual(a, b));
    }

    [Fact]
    public void AreEqual_Field_TypeDifference_ReturnsFalse()
    {
        var a = FirstMember("class C { int x; }");
        var b = FirstMember("class C { string x; }");
        Assert.False(MemberSignatureComparer.AreEqual(a, b));
    }

    [Fact]
    public void AreEqual_IdenticalMethod_ReturnsTrue()
    {
        var a = FirstMember("class C { public void Foo(int x) { } }");
        var b = FirstMember("class C { public void Foo(int x) { } }");
        Assert.True(MemberSignatureComparer.AreEqual(a, b));
    }

    [Fact]
    public void AreEqual_DifferentBodies_SameSignature_ReturnsTrue()
    {
        // Body changes do not affect signature equality.
        var a = FirstMember("class C { int Foo() { return 1; } }");
        var b = FirstMember("class C { int Foo() { return 42; } }");
        Assert.True(MemberSignatureComparer.AreEqual(a, b));
    }
}
