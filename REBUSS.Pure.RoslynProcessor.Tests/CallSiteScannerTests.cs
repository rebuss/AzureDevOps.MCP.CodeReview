using Microsoft.Extensions.Logging.Abstractions;
using REBUSS.Pure.RoslynProcessor;

namespace REBUSS.Pure.RoslynProcessor.Tests;

public class CallSiteScannerTests : IDisposable
{
    private readonly CallSiteScanner _scanner = new(NullLogger<CallSiteScanner>.Instance);
    private readonly string _tempDir;

    public CallSiteScannerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"callsite-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    private void WriteFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_tempDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    [Fact]
    public async Task ScanAsync_MethodCalledInOtherFile_ReturnsLocation()
    {
        WriteFile("src/Caller.cs", "class Caller { void Run() { svc.ProcessOrder(order); } }");
        var targets = new[] { new CallSiteTarget { Name = "ProcessOrder", Reason = "changed" } };

        var results = await _scanner.ScanAsync(_tempDir, targets, null, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal("ProcessOrder", result.TargetName);
        Assert.True(result.TotalCount > 0);
        Assert.Contains(result.Locations, l => l.FilePath.Contains("Caller.cs"));
    }

    [Fact]
    public async Task ScanAsync_MethodCalledMultipleTimes_ReturnsAllLocations()
    {
        WriteFile("src/Caller.cs", @"class Caller {
    void A() { svc.ProcessOrder(a); }
    void B() { svc.ProcessOrder(b); }
    void C() { svc.ProcessOrder(c); }
}");
        var targets = new[] { new CallSiteTarget { Name = "ProcessOrder", Reason = "changed" } };

        var results = await _scanner.ScanAsync(_tempDir, targets, null, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.True(result.TotalCount >= 3);
    }

    [Fact]
    public async Task ScanAsync_MethodInComment_NotReturned()
    {
        WriteFile("src/File.cs", "class C { // ProcessOrder was removed\n}");
        var targets = new[] { new CallSiteTarget { Name = "ProcessOrder", Reason = "changed" } };

        var results = await _scanner.ScanAsync(_tempDir, targets, null, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task ScanAsync_MethodInString_NotReturned()
    {
        WriteFile("src/File.cs", "class C { string s = \"ProcessOrder\"; }");
        var targets = new[] { new CallSiteTarget { Name = "ProcessOrder", Reason = "changed" } };

        var results = await _scanner.ScanAsync(_tempDir, targets, null, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task ScanAsync_CurrentFileExcluded_NotReturned()
    {
        WriteFile("src/OrderService.cs", "class Svc { void Run() { ProcessOrder(x); } }");
        var targets = new[] { new CallSiteTarget { Name = "ProcessOrder", Reason = "changed" } };

        var results = await _scanner.ScanAsync(_tempDir, targets, "src/OrderService.cs", CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task ScanAsync_GeneratedFileExcluded_NotReturned()
    {
        WriteFile("src/File.Designer.cs", "class C { void Run() { svc.ProcessOrder(x); } }");
        var targets = new[] { new CallSiteTarget { Name = "ProcessOrder", Reason = "changed" } };

        var results = await _scanner.ScanAsync(_tempDir, targets, null, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task ScanAsync_ObjBinDirectoryExcluded_NotReturned()
    {
        WriteFile("obj/Debug/File.cs", "class C { void Run() { svc.ProcessOrder(x); } }");
        WriteFile("bin/Debug/File.cs", "class C { void Run() { svc.ProcessOrder(x); } }");
        var targets = new[] { new CallSiteTarget { Name = "ProcessOrder", Reason = "changed" } };

        var results = await _scanner.ScanAsync(_tempDir, targets, null, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task ScanAsync_FileTooLarge_Excluded()
    {
        WriteFile("src/Large.cs", $"class C {{ void Run() {{ svc.ProcessOrder(x); }} }}{new string(' ', 101_000)}");
        var targets = new[] { new CallSiteTarget { Name = "ProcessOrder", Reason = "changed" } };

        var results = await _scanner.ScanAsync(_tempDir, targets, null, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task ScanAsync_NoCallSites_ReturnsEmptyLocations()
    {
        WriteFile("src/Other.cs", "class Other { void Run() { } }");
        var targets = new[] { new CallSiteTarget { Name = "ProcessOrder", Reason = "changed" } };

        var results = await _scanner.ScanAsync(_tempDir, targets, null, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Locations);
    }

    [Fact]
    public async Task ScanAsync_ExceedsMaxPerTarget_TruncatesWithTotalCount()
    {
        // Create 15 call sites across different files
        for (int i = 0; i < 15; i++)
            WriteFile($"src/Caller{i}.cs", $"class C{i} {{ void Run() {{ svc.ProcessOrder(x); }} }}");

        var targets = new[] { new CallSiteTarget { Name = "ProcessOrder", Reason = "changed" } };

        var results = await _scanner.ScanAsync(_tempDir, targets, null, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(15, result.TotalCount);
        Assert.Equal(10, result.Locations.Count);
    }

    [Fact]
    public async Task ScanAsync_Cancellation_PropagatesCorrectly()
    {
        WriteFile("src/File.cs", "class C { void Run() { svc.ProcessOrder(x); } }");
        var targets = new[] { new CallSiteTarget { Name = "ProcessOrder", Reason = "changed" } };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _scanner.ScanAsync(_tempDir, targets, null, cts.Token));
    }

    [Fact]
    public async Task ScanAsync_VariableWithSameName_NotReturned()
    {
        WriteFile("src/File.cs", "class C { void Run() { var ProcessOrder = 5; _ = ProcessOrder; } }");
        var targets = new[] { new CallSiteTarget { Name = "ProcessOrder", Reason = "changed" } };

        var results = await _scanner.ScanAsync(_tempDir, targets, null, CancellationToken.None);

        // Variable assignment produces IdentifierNameSyntax but parent is not invocation/member-access
        // The `_ = ProcessOrder` is a simple name, not in invocation context — should be filtered out
        // However, `var ProcessOrder = 5` may or may not produce an IdentifierNameSyntax depending on context
        // The key guarantee: no InvocationExpression or MemberAccessExpression parents
        var result = Assert.Single(results);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task ScanAsync_ConstructorUsage_ReturnsLocation()
    {
        WriteFile("src/Caller.cs", "class Caller { void Run() { var svc = new OrderService(); } }");
        var targets = new[] { new CallSiteTarget { Name = "OrderService", Reason = "new type", Kind = CallSiteTargetKind.Type } };

        var results = await _scanner.ScanAsync(_tempDir, targets, null, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.True(result.TotalCount > 0, "Constructor usage (new OrderService()) should be detected");
    }

    [Fact]
    public async Task ScanAsync_BaseClassReference_ReturnsLocation()
    {
        WriteFile("src/Derived.cs", "class Derived : OrderService { }");
        var targets = new[] { new CallSiteTarget { Name = "OrderService", Reason = "new type", Kind = CallSiteTargetKind.Type } };

        var results = await _scanner.ScanAsync(_tempDir, targets, null, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.True(result.TotalCount > 0, "Base class reference should be detected");
    }

    [Fact]
    public async Task ScanAsync_ReceiverWithSameNameAsTarget_NotCountedAsCallSite()
    {
        // Regression: `obj.Something()` where `obj` and `Something` are both
        // IdentifierNames under a MemberAccessExpression. Only the Name slot
        // (member being accessed) should be counted — the receiver (obj) is a
        // local variable unrelated to the method we track.
        WriteFile("src/Caller.cs", @"class Caller {
    void Run() {
        var ProcessOrder = new object();     // local variable with target name
        ProcessOrder.ToString();             // ProcessOrder as receiver — must NOT count
        ProcessOrder.Equals(this);           // same
    }
}");
        var targets = new[] { new CallSiteTarget { Name = "ProcessOrder", Reason = "changed" } };

        var results = await _scanner.ScanAsync(_tempDir, targets, null, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Locations);
    }

    [Fact]
    public async Task ScanAsync_MemberAccessWithTargetAsName_Counted()
    {
        // Positive side of the same fix: when the target name appears as the
        // member slot (`obj.ProcessOrder`), it must still be counted.
        WriteFile("src/Caller.cs", "class Caller { void Run(object svc) { svc.ProcessOrder(); } }");
        var targets = new[] { new CallSiteTarget { Name = "ProcessOrder", Reason = "changed" } };

        var results = await _scanner.ScanAsync(_tempDir, targets, null, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(1, result.TotalCount);
    }
}
