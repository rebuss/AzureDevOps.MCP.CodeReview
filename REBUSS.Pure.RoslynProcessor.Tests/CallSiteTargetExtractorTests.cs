using REBUSS.Pure.RoslynProcessor;

namespace REBUSS.Pure.RoslynProcessor.Tests;

public class CallSiteTargetExtractorTests
{
    [Fact]
    public void ExtractTargets_MethodSignatureChanged_ReturnsTarget()
    {
        var diff = "=== src/Svc.cs (edit) ===\n[structural-changes]\n  \U0001f504 Method signature changed: ProcessOrder(Order) \u2192 ProcessOrder(Order, CancellationToken)\n[/structural-changes]\n@@ -1,1 +1,1 @@";
        var targets = CallSiteTargetExtractor.ExtractTargets(diff);

        var target = Assert.Single(targets);
        Assert.Equal("ProcessOrder", target.Name);
        Assert.Equal("signature changed", target.Reason);
        Assert.Equal(CallSiteTargetKind.Method, target.Kind);
    }

    [Fact]
    public void ExtractTargets_NewMethod_ReturnsTarget()
    {
        var diff = "=== src/Svc.cs (edit) ===\n[structural-changes]\n  \u2795 New method: ValidateOrder(Order) : bool\n[/structural-changes]\n@@ -1,1 +1,1 @@";
        var targets = CallSiteTargetExtractor.ExtractTargets(diff);

        var target = Assert.Single(targets);
        Assert.Equal("ValidateOrder", target.Name);
        Assert.Equal("new method", target.Reason);
    }

    [Fact]
    public void ExtractTargets_RemovedMethod_Skipped()
    {
        var diff = "=== src/Svc.cs (edit) ===\n[structural-changes]\n  \u26a0 Public method removed: ProcessLegacy(int)\n[/structural-changes]\n@@ -1,1 +1,1 @@";
        var targets = CallSiteTargetExtractor.ExtractTargets(diff);

        Assert.Empty(targets);
    }

    [Fact]
    public void ExtractTargets_ConstructorChanged_ReturnsCtorTarget()
    {
        var diff = "=== src/Svc.cs (edit) ===\n[structural-changes]\n  \U0001f504 Constructor changed: (IService) \u2192 (IService, ILogger)\n[/structural-changes]\n@@ -1,1 +1,1 @@";
        var targets = CallSiteTargetExtractor.ExtractTargets(diff);

        var target = Assert.Single(targets);
        Assert.Equal(".ctor", target.Name);
        Assert.Equal(CallSiteTargetKind.Constructor, target.Kind);
    }

    [Fact]
    public void ExtractTargets_NewClass_ReturnsTypeTarget()
    {
        var diff = "=== src/Svc.cs (edit) ===\n[structural-changes]\n  \u2795 New class: OrderValidator\n[/structural-changes]\n@@ -1,1 +1,1 @@";
        var targets = CallSiteTargetExtractor.ExtractTargets(diff);

        var target = Assert.Single(targets);
        Assert.Equal("OrderValidator", target.Name);
        Assert.Equal("new type", target.Reason);
        Assert.Equal(CallSiteTargetKind.Type, target.Kind);
    }

    [Fact]
    public void ExtractTargets_NoStructuralChangesBlock_FallbackToHeuristic()
    {
        var diff = "=== src/Svc.cs (edit) ===\n@@ -1,1 +1,3 @@\n+    public bool Validate(Order o)\n+    {\n+    }";
        var targets = CallSiteTargetExtractor.ExtractTargets(diff);

        var target = Assert.Single(targets);
        Assert.Equal("Validate", target.Name);
        Assert.Equal("changed method", target.Reason);
    }

    [Fact]
    public void ExtractTargets_EmptyDiff_ReturnsEmpty()
    {
        Assert.Empty(CallSiteTargetExtractor.ExtractTargets(""));
    }

    [Fact]
    public void ExtractTargets_MultipleChanges_ReturnsAll()
    {
        var diff = "=== src/Svc.cs (edit) ===\n[structural-changes]\n  \U0001f504 Method signature changed: ProcessOrder(\n  \u2795 New method: ValidateOrder(\n  \u2795 New class: OrderValidator\n[/structural-changes]\n@@";
        var targets = CallSiteTargetExtractor.ExtractTargets(diff);

        Assert.Equal(3, targets.Count);
    }

    [Fact]
    public void ExtractTargets_MethodNameContainsRemoved_StillExtracted()
    {
        // Regression: method named "ProcessRemoved" should NOT be skipped
        // The "removed" keyword check should only match the ⚠ emoji prefix, not method names
        var diff = "=== src/Svc.cs (edit) ===\n[structural-changes]\n  \U0001f504 Method signature changed: ProcessRemoved(Order) \u2192 ProcessRemoved(Order, Token)\n[/structural-changes]\n@@";
        var targets = CallSiteTargetExtractor.ExtractTargets(diff);

        var target = Assert.Single(targets);
        Assert.Equal("ProcessRemoved", target.Name);
        Assert.Equal("signature changed", target.Reason);
    }
}
