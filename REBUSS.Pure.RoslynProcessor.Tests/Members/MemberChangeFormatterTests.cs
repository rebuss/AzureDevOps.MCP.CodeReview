using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using REBUSS.Pure.RoslynProcessor.Members;

namespace REBUSS.Pure.RoslynProcessor.Tests.Members;

/// <summary>
/// Focused unit tests for <see cref="MemberChangeFormatter"/>. Description strings
/// are user-visible — exact-character preservation matters.
/// </summary>
public class MemberChangeFormatterTests
{
    private static MemberDeclarationSyntax FirstMember(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        return root.DescendantNodes().OfType<TypeDeclarationSyntax>().First().Members.First();
    }

    [Fact]
    public void FormatAdded_Method_IncludesParamsAndReturnType()
    {
        var member = FirstMember("class C { int Foo(string s, int n) { return 0; } }");
        Assert.Equal("New method: Foo(string, int) : int", MemberChangeFormatter.FormatAdded(member));
    }

    [Fact]
    public void FormatAdded_Property_IncludesType()
    {
        var member = FirstMember("class C { public string Name { get; set; } }");
        Assert.Equal("New property: Name : string", MemberChangeFormatter.FormatAdded(member));
    }

    [Fact]
    public void FormatAdded_Field_IncludesType()
    {
        var member = FirstMember("class C { private int _count; }");
        Assert.Equal("New field: _count : int", MemberChangeFormatter.FormatAdded(member));
    }

    [Fact]
    public void FormatRemoved_PrefixesVisibility()
    {
        var member = FirstMember("class C { public void Foo() { } }");
        Assert.Equal("Public method removed: Foo()", MemberChangeFormatter.FormatRemoved(member));
    }

    [Fact]
    public void FormatRemoved_NoVisibilityModifier_OmitsPrefix()
    {
        var member = FirstMember("class C { void Foo() { } }");
        Assert.Equal("method removed: Foo()", MemberChangeFormatter.FormatRemoved(member));
    }

    [Fact]
    public void FormatSignatureChanged_Method_RendersBeforeAfterArrow()
    {
        var before = FirstMember("class C { void Foo(int a) { } }");
        var after = FirstMember("class C { void Foo(int a, string b) { } }");
        Assert.Equal(
            "Method signature changed: Foo(int) → Foo(int, string)",
            MemberChangeFormatter.FormatSignatureChanged(before, after, "method:Foo(int,string)"));
    }

    [Fact]
    public void FormatSignatureChanged_Property_RendersTypeChange()
    {
        var before = FirstMember("class C { public int Count { get; set; } }");
        var after = FirstMember("class C { public long Count { get; set; } }");
        Assert.Equal(
            "Property changed: Count : int → long",
            MemberChangeFormatter.FormatSignatureChanged(before, after, "property:Count"));
    }

    [Fact]
    public void FormatParams_TruncatesAtFour_PreservesFirstTwoTypes()
    {
        var member = (MethodDeclarationSyntax)FirstMember(
            "class C { void Foo(int a, string b, double c, char d, byte e) { } }");
        Assert.Equal("int, string, ... +3", MemberChangeFormatter.FormatParams(member.ParameterList));
    }

    [Fact]
    public void FormatParams_AtThree_StillFull()
    {
        var member = (MethodDeclarationSyntax)FirstMember(
            "class C { void Foo(int a, string b, double c) { } }");
        Assert.Equal("int, string, double", MemberChangeFormatter.FormatParams(member.ParameterList));
    }

    [Fact]
    public void GetVisibility_PerModifier()
    {
        var publicM = FirstMember("class C { public void M() { } }");
        var protectedM = FirstMember("class C { protected void M() { } }");
        var internalM = FirstMember("class C { internal void M() { } }");
        var privateM = FirstMember("class C { private void M() { } }");

        Assert.Equal("Public ", MemberChangeFormatter.GetVisibility(publicM.Modifiers));
        Assert.Equal("Protected ", MemberChangeFormatter.GetVisibility(protectedM.Modifiers));
        Assert.Equal("Internal ", MemberChangeFormatter.GetVisibility(internalM.Modifiers));
        Assert.Equal("", MemberChangeFormatter.GetVisibility(privateM.Modifiers));
    }
}
