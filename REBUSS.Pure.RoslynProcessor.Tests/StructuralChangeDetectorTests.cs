using Microsoft.CodeAnalysis.CSharp;
using REBUSS.Pure.RoslynProcessor;

namespace REBUSS.Pure.RoslynProcessor.Tests;

public class StructuralChangeDetectorTests
{
    private static IReadOnlyList<StructuralChange> Detect(string before, string after)
    {
        var beforeTree = CSharpSyntaxTree.ParseText(before);
        var afterTree = CSharpSyntaxTree.ParseText(after);
        return StructuralChangeDetector.DetectChanges(beforeTree, afterTree);
    }

    [Fact]
    public void DetectChanges_MethodSignatureChanged_ReturnsSignatureChanged()
    {
        var before = "class C { void Process(Order o) { } }";
        var after = "class C { void Process(Order o, CancellationToken ct) { } }";

        var changes = Detect(before, after);

        var change = Assert.Single(changes);
        Assert.Equal(StructuralChangeKind.SignatureChanged, change.Kind);
        Assert.Contains("Process", change.Description);
        Assert.Contains("\u2192", change.Description); // →
    }

    [Fact]
    public void DetectChanges_MethodAdded_ReturnsMemberAdded()
    {
        var before = "class C { }";
        var after = "class C { public bool Validate(Order o) { return true; } }";

        var changes = Detect(before, after);

        var change = Assert.Single(changes);
        Assert.Equal(StructuralChangeKind.MemberAdded, change.Kind);
        Assert.Contains("Validate", change.Description);
    }

    [Fact]
    public void DetectChanges_MethodRemoved_ReturnsMemberRemoved()
    {
        var before = "class C { public void ProcessLegacy(int id) { } }";
        var after = "class C { }";

        var changes = Detect(before, after);

        var change = Assert.Single(changes);
        Assert.Equal(StructuralChangeKind.MemberRemoved, change.Kind);
        Assert.Contains("ProcessLegacy", change.Description);
    }

    [Fact]
    public void DetectChanges_ModifierChanged_ReturnsSignatureChanged()
    {
        var before = "class C { public void Run() { } }";
        var after = "class C { private void Run() { } }";

        var changes = Detect(before, after);

        var change = Assert.Single(changes);
        Assert.Equal(StructuralChangeKind.SignatureChanged, change.Kind);
    }

    [Fact]
    public void DetectChanges_AsyncAdded_ReturnsSignatureChanged()
    {
        var before = "class C { void Run() { } }";
        var after = "class C { async Task Run() { } }";

        var changes = Detect(before, after);

        var change = Assert.Single(changes);
        Assert.Equal(StructuralChangeKind.SignatureChanged, change.Kind);
    }

    [Fact]
    public void DetectChanges_NewClass_ReturnsTypeAdded()
    {
        var before = "";
        var after = "class OrderValidator { }";

        var changes = Detect(before, after);

        var change = Assert.Single(changes);
        Assert.Equal(StructuralChangeKind.TypeAdded, change.Kind);
        Assert.Contains("OrderValidator", change.Description);
    }

    [Fact]
    public void DetectChanges_ClassRemoved_ReturnsTypeRemoved()
    {
        var before = "class LegacyDto { }";
        var after = "";

        var changes = Detect(before, after);

        var change = Assert.Single(changes);
        Assert.Equal(StructuralChangeKind.TypeRemoved, change.Kind);
        Assert.Contains("LegacyDto", change.Description);
    }

    [Fact]
    public void DetectChanges_BaseTypeChanged_ReturnsBaseTypeChanged()
    {
        var before = "class C : ControllerBase { }";
        var after = "class C : Controller { }";

        var changes = Detect(before, after);

        var change = Assert.Single(changes);
        Assert.Equal(StructuralChangeKind.BaseTypeChanged, change.Kind);
        Assert.Contains("ControllerBase", change.Description);
        Assert.Contains("Controller", change.Description);
    }

    [Fact]
    public void DetectChanges_InterfaceAdded_ReturnsBaseTypeChanged()
    {
        var before = "class C { }";
        var after = "class C : IDisposable { }";

        var changes = Detect(before, after);

        var change = Assert.Single(changes);
        Assert.Equal(StructuralChangeKind.BaseTypeChanged, change.Kind);
        Assert.Contains("IDisposable", change.Description);
    }

    [Fact]
    public void DetectChanges_PropertyAdded_ReturnsMemberAdded()
    {
        var before = "class C { }";
        var after = "class C { public bool IsValid { get; } }";

        var changes = Detect(before, after);

        var change = Assert.Single(changes);
        Assert.Equal(StructuralChangeKind.MemberAdded, change.Kind);
        Assert.Contains("IsValid", change.Description);
    }

    [Fact]
    public void DetectChanges_ConstructorParameterAdded_ReturnsSignatureChanged()
    {
        var before = "class C { public C(IService svc) { } }";
        var after = "class C { public C(IService svc, ILogger<C> logger) { } }";

        var changes = Detect(before, after);

        var change = Assert.Single(changes);
        Assert.Equal(StructuralChangeKind.SignatureChanged, change.Kind);
        Assert.Contains("Constructor", change.Description);
    }

    [Fact]
    public void DetectChanges_BodyOnlyChange_ReturnsEmpty()
    {
        var before = "class C { void Run() { Console.WriteLine(\"old\"); } }";
        var after = "class C { void Run() { Console.WriteLine(\"new\"); } }";

        var changes = Detect(before, after);

        Assert.Empty(changes);
    }

    [Fact]
    public void DetectChanges_IdenticalTrees_ReturnsEmpty()
    {
        var code = "class C { void Run() { } }";

        var changes = Detect(code, code);

        Assert.Empty(changes);
    }

    [Fact]
    public void DetectChanges_EmptyBefore_AllMembersAdded()
    {
        var before = "";
        var after = "class C { void A() { } void B() { } }";

        var changes = Detect(before, after);

        Assert.Contains(changes, c => c.Kind == StructuralChangeKind.TypeAdded);
    }

    [Fact]
    public void DetectChanges_EmptyAfter_AllMembersRemoved()
    {
        var before = "class C { void A() { } void B() { } }";
        var after = "";

        var changes = Detect(before, after);

        Assert.Contains(changes, c => c.Kind == StructuralChangeKind.TypeRemoved);
    }

    [Fact]
    public void DetectChanges_NewOverloadAdded_DetectedAsMemberAdded()
    {
        // New Process(bool) overload added while existing overloads are unchanged.
        // Previously this was invisible — name-only key collided with first overload.
        var before = "class C { void Process(int x) { } void Process(string s) { } }";
        var after = "class C { void Process(int x) { } void Process(string s) { } void Process(bool b) { } }";

        var changes = Detect(before, after);

        Assert.Contains(changes, c => c.Kind == StructuralChangeKind.MemberAdded
                                      && c.Description.Contains("Process"));
    }

    [Fact]
    public void DetectChanges_OverloadRemoved_DetectedAsMemberRemoved()
    {
        // Remove the string overload; int overload remains. Previously: undetected.
        var before = "class C { void Process(int x) { } void Process(string s) { } }";
        var after = "class C { void Process(int x) { } }";

        var changes = Detect(before, after);

        Assert.Contains(changes, c => c.Kind == StructuralChangeKind.MemberRemoved
                                      && c.Description.Contains("Process"));
    }

    [Fact]
    public void DetectChanges_OverloadSignatureChangeOnNonFirst_Detected()
    {
        // Return type change on the string overload — previously invisible because
        // the first occurrence (int overload) won the key collision.
        var before = "class C { void Process(int x) { } void Process(string s) { } }";
        var after = "class C { void Process(int x) { } int Process(string s) { return 0; } }";

        var changes = Detect(before, after);

        Assert.Contains(changes, c => c.Kind == StructuralChangeKind.SignatureChanged
                                      && c.Description.Contains("Process"));
    }

    [Fact]
    public void DetectChanges_ConstructorOverloadAdded_Detected()
    {
        var before = "class C { public C(int x) { } }";
        var after = "class C { public C(int x) { } public C(string s) { } }";

        var changes = Detect(before, after);

        Assert.Contains(changes, c => c.Kind == StructuralChangeKind.MemberAdded);
    }

    [Fact]
    public void DetectChanges_FieldAdded_ReturnsMemberAdded()
    {
        var before = "class C { }";
        var after = "class C { private readonly int _count; }";

        var changes = Detect(before, after);

        var change = Assert.Single(changes);
        Assert.Equal(StructuralChangeKind.MemberAdded, change.Kind);
        Assert.Contains("_count", change.Description);
    }

    [Fact]
    public void DetectChanges_DuplicateSimpleNameInDifferentNamespaces_DoesNotThrow()
    {
        var before = "namespace A { class Foo { } } namespace B { class Foo { } }";
        var after = "namespace A { class Foo { public int X; } } namespace B { class Foo { } }";

        var changes = Detect(before, after);

        Assert.Contains(changes, c => c.Kind == StructuralChangeKind.MemberAdded
                                      && c.Description.Contains("X"));
    }

    [Fact]
    public void DetectChanges_ReturnTypeChanged_ReturnsSignatureChanged()
    {
        var before = "class C { void Run() { } }";
        var after = "class C { int Run() { return 0; } }";

        var changes = Detect(before, after);

        var change = Assert.Single(changes);
        Assert.Equal(StructuralChangeKind.SignatureChanged, change.Kind);
    }
}
