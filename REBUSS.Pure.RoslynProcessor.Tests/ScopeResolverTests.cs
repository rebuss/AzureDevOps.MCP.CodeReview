using Microsoft.CodeAnalysis.CSharp;
using REBUSS.Pure.RoslynProcessor;

namespace REBUSS.Pure.RoslynProcessor.Tests;

public class ScopeResolverTests
{
    private static string? Resolve(string source, int line)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        return ScopeResolver.Resolve(tree.GetRoot(), line);
    }

    [Fact]
    public void Resolve_LineInsideMethod_ReturnsClassAndMethod()
    {
        var source = @"
class OrderService
{
    void ProcessOrder(Order o)
    {
        var x = 1; // line 6
    }
}";
        var result = Resolve(source, 6);
        Assert.NotNull(result);
        Assert.Contains("OrderService", result);
        Assert.Contains("ProcessOrder", result);
        Assert.Contains("Order", result);
    }

    [Fact]
    public void Resolve_LineInsideConstructor_ReturnsClassAndCtor()
    {
        var source = @"
class OrderService
{
    public OrderService(ILogger logger)
    {
        _ = logger; // line 6
    }
}";
        var result = Resolve(source, 6);
        Assert.NotNull(result);
        Assert.Contains("OrderService.ctor", result);
        Assert.Contains("ILogger", result);
    }

    [Fact]
    public void Resolve_LineInsideProperty_ReturnsClassAndProperty()
    {
        var source = @"
class OrderService
{
    public bool IsValid
    {
        get { return true; } // line 6
    }
}";
        var result = Resolve(source, 6);
        Assert.Equal("OrderService.IsValid", result);
    }

    [Fact]
    public void Resolve_LineInsideLocalFunction_ReturnsFullChain()
    {
        var source = @"
class OrderService
{
    void Process()
    {
        void ValidateLocal(Item item)
        {
            _ = item; // line 8
        }
    }
}";
        var result = Resolve(source, 8);
        Assert.NotNull(result);
        Assert.Contains("OrderService", result);
        Assert.Contains("Process", result);
        Assert.Contains("ValidateLocal", result);
    }

    [Fact]
    public void Resolve_LineAtClassLevel_ReturnsClassName()
    {
        var source = @"
class OrderService
{
    private readonly int _count = 0; // line 4
}";
        var result = Resolve(source, 4);
        Assert.Equal("OrderService", result);
    }

    [Fact]
    public void Resolve_LineInNestedClass_ReturnsFullPath()
    {
        var source = @"
class OuterClass
{
    class InnerClass
    {
        void Method() { } // line 6
    }
}";
        var result = Resolve(source, 6);
        Assert.NotNull(result);
        Assert.Contains("OuterClass.InnerClass", result);
    }

    [Fact]
    public void Resolve_LineInNamespace_ReturnsNamespace()
    {
        var source = @"
namespace REBUSS.Pure.Services
{
    // some comment at line 4 — outside any type
}";
        var result = Resolve(source, 4);
        Assert.Equal("REBUSS.Pure.Services", result);
    }

    [Fact]
    public void Resolve_MethodWithManyParams_TruncatesAfterFour()
    {
        var source = @"
class Svc
{
    void Method(int a, string b, bool c, Order d, decimal e, float f)
    {
        _ = a; // line 6
    }
}";
        var result = Resolve(source, 6);
        Assert.NotNull(result);
        Assert.Contains("... +2", result);
    }

    [Fact]
    public void Resolve_GenericMethod_IncludesTypeParams()
    {
        var source = @"
class Svc
{
    TDest Convert<TSource, TDest>(TSource src)
    {
        return default; // line 6
    }
}";
        var result = Resolve(source, 6);
        Assert.NotNull(result);
        Assert.Contains("Convert<TSource, TDest>", result);
    }

    [Fact]
    public void Resolve_LineOutOfRange_ReturnsNull()
    {
        var source = "class C { }";
        Assert.Null(Resolve(source, 999));
    }

    [Fact]
    public void Resolve_EmptyFile_ReturnsNull()
    {
        Assert.Null(Resolve("", 1));
    }

    [Fact]
    public void Resolve_LineInIndexer_ReturnsIndexerSignature()
    {
        var source = @"
class Collection
{
    public int this[int index]
    {
        get { return 0; } // line 6
    }
}";
        var result = Resolve(source, 6);
        Assert.NotNull(result);
        Assert.Contains("Collection", result);
        Assert.Contains("this[", result);
    }

    [Fact]
    public void Resolve_LineInEventDeclaration_ReturnsEventScope()
    {
        var source = @"
class Svc
{
    public event EventHandler Changed
    {
        add { } // line 6
        remove { }
    }
}";
        var result = Resolve(source, 6);
        Assert.NotNull(result);
        Assert.Contains("Svc", result);
        Assert.Contains("Changed", result);
    }

    [Fact]
    public void Resolve_LineInOperator_ReturnsOperatorScope()
    {
        var source = @"
class Money
{
    public static Money operator +(Money a, Money b)
    {
        return a; // line 6
    }
}";
        var result = Resolve(source, 6);
        Assert.NotNull(result);
        Assert.Contains("Money", result);
        Assert.Contains("operator", result);
    }

    [Fact]
    public void Resolve_LineInDestructor_ReturnsDestructorScope()
    {
        var source = @"
class Resource
{
    ~Resource()
    {
        // cleanup line 6
    }
}";
        var result = Resolve(source, 6);
        Assert.NotNull(result);
        Assert.Contains("Resource", result);
    }
}
