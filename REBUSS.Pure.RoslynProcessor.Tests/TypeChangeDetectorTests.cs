using Microsoft.CodeAnalysis.CSharp;

namespace REBUSS.Pure.RoslynProcessor.Tests;

/// <summary>
/// Focused unit tests for <see cref="TypeChangeDetector.Detect"/>. Verifies the
/// set-diff dispatch over a pair of <see cref="TypeDeclarationIndex"/>.
/// </summary>
public class TypeChangeDetectorTests
{
    private static TypeDeclarationIndex IndexOf(string source) =>
        TypeDeclarationIndex.BuildFrom(CSharpSyntaxTree.ParseText(source).GetRoot());

    [Fact]
    public void Detect_AddedType_EmitsTypeAddedWithKindNameAndLineNumber()
    {
        var before = IndexOf("class A { }");
        var after = IndexOf("class A { }\nclass B { }");

        var change = Assert.Single(TypeChangeDetector.Detect(before, after));

        Assert.Equal(StructuralChangeKind.TypeAdded, change.Kind);
        Assert.Equal("New class: B", change.Description);
        Assert.NotNull(change.LineNumber);
    }

    [Fact]
    public void Detect_RemovedType_EmitsTypeRemovedWithNullLineNumber()
    {
        var before = IndexOf("class A { }\nclass B { }");
        var after = IndexOf("class A { }");

        var change = Assert.Single(TypeChangeDetector.Detect(before, after));

        Assert.Equal(StructuralChangeKind.TypeRemoved, change.Kind);
        Assert.Equal("class removed: B", change.Description);
        Assert.Null(change.LineNumber);
    }

    [Fact]
    public void Detect_MatchedTypes_EmitsNothing()
    {
        // Set-diff over identical names → no add/remove events.
        var before = IndexOf("class A { } class B { }");
        var after = IndexOf("class A { } class B { }");

        Assert.Empty(TypeChangeDetector.Detect(before, after));
    }

    [Fact]
    public void Detect_RecordTypeAdded_RendersRecordKind()
    {
        var before = IndexOf("class A { }");
        var after = IndexOf("class A { }\nrecord R();");

        var change = Assert.Single(TypeChangeDetector.Detect(before, after));

        Assert.Equal("New record: R", change.Description);
    }
}
