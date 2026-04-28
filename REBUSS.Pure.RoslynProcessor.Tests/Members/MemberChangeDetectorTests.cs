using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using REBUSS.Pure.RoslynProcessor.Members;

namespace REBUSS.Pure.RoslynProcessor.Tests.Members;

/// <summary>
/// Focused unit tests for <see cref="MemberChangeDetector.Detect"/>. Pinpoint the
/// non-trivial heuristics: sole-overload signature edit pairs as one
/// <c>SignatureChanged</c>; real overload addition/removal does not pair; matched-key
/// signature differences emit <c>SignatureChanged</c>; modifier-only changes are
/// reported.
/// </summary>
public class MemberChangeDetectorTests
{
    private static TypeDeclarationSyntax FirstType(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        return root.DescendantNodes().OfType<TypeDeclarationSyntax>().First();
    }

    [Fact]
    public void Detect_SoleOverloadParameterEdit_PairsAsSignatureChanged()
    {
        // The single Foo had its parameter list edited — orphan-pairing heuristic
        // fires: report as one SignatureChanged, not Remove + Add.
        var before = FirstType("class C { void Foo(int x) { } }");
        var after = FirstType("class C { void Foo(int x, string y) { } }");

        var change = Assert.Single(MemberChangeDetector.Detect(before, after));

        Assert.Equal(StructuralChangeKind.SignatureChanged, change.Kind);
        Assert.Contains("Foo(int)", change.Description);
        Assert.Contains("Foo(int, string)", change.Description);
    }

    [Fact]
    public void Detect_OverloadAdded_DoesNotPair_EmitsMemberAddedOnly()
    {
        // Foo(int) stays unchanged in both before and after; a new overload Foo(string) is
        // added. Group has 2 entries on the after side — orphan-pair refuses to fire,
        // so the new overload reads as plain MemberAdded.
        var before = FirstType("class C { void Foo(int x) { } }");
        var after = FirstType("class C { void Foo(int x) { } void Foo(string x) { } }");

        var changes = MemberChangeDetector.Detect(before, after).ToList();

        var added = Assert.Single(changes);
        Assert.Equal(StructuralChangeKind.MemberAdded, added.Kind);
        Assert.Contains("Foo(string)", added.Description);
    }

    [Fact]
    public void Detect_MatchedKeySignatureDiffer_EmitsSignatureChanged()
    {
        // Same name + same param types → keys match → signature comparer fires on
        // modifier difference (added 'static').
        var before = FirstType("class C { void Foo(int x) { } }");
        var after = FirstType("class C { static void Foo(int x) { } }");

        var change = Assert.Single(MemberChangeDetector.Detect(before, after));

        Assert.Equal(StructuralChangeKind.SignatureChanged, change.Kind);
    }

    [Fact]
    public void Detect_PropertyTypeChanged_EmitsSignatureChanged()
    {
        // Property keyed by name only — type change shows up only via SignatureComparer.
        var before = FirstType("class C { public int X { get; set; } }");
        var after = FirstType("class C { public string X { get; set; } }");

        var change = Assert.Single(MemberChangeDetector.Detect(before, after));

        Assert.Equal(StructuralChangeKind.SignatureChanged, change.Kind);
        Assert.Equal("Property changed: X : int → string", change.Description);
    }

    [Fact]
    public void Detect_AllMembersRemoved_EmitsMemberRemovedPerMember_NullLineNumber()
    {
        var before = FirstType("class C { void A() { } int B; }");
        var after = FirstType("class C { }");

        var changes = MemberChangeDetector.Detect(before, after).ToList();

        Assert.Equal(2, changes.Count);
        Assert.All(changes, c => Assert.Equal(StructuralChangeKind.MemberRemoved, c.Kind));
        Assert.All(changes, c => Assert.Null(c.LineNumber));
    }

    [Fact]
    public void Detect_NoChanges_ReturnsEmpty()
    {
        var before = FirstType("class C { public int X { get; set; } void M() { } }");
        var after = FirstType("class C { public int X { get; set; } void M() { } }");

        Assert.Empty(MemberChangeDetector.Detect(before, after));
    }
}
