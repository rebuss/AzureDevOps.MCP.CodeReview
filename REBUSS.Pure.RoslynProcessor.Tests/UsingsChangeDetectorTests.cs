using REBUSS.Pure.RoslynProcessor;

namespace REBUSS.Pure.RoslynProcessor.Tests;

public class UsingsChangeDetectorTests
{
    [Fact]
    public void ExtractUsings_StandardUsings_ReturnsNamespaces()
    {
        var source = "using System;\nusing System.Linq;\nclass C { }";
        var result = UsingsChangeDetector.ExtractUsings(source);
        Assert.Equal(new[] { "System", "System.Linq" }, result);
    }

    [Fact]
    public void ExtractUsings_StaticUsing_IncludesPrefix()
    {
        var source = "using static System.Math;\nclass C { }";
        var result = UsingsChangeDetector.ExtractUsings(source);
        Assert.Contains("static System.Math", result);
    }

    [Fact]
    public void ExtractUsings_AliasUsing_IncludesAlias()
    {
        var source = "using Alias = System.IO;\nclass C { }";
        var result = UsingsChangeDetector.ExtractUsings(source);
        Assert.Contains("Alias = System.IO", result);
    }

    [Fact]
    public void ExtractUsings_GlobalUsing_IncludesPrefix()
    {
        var source = "global using System;\nclass C { }";
        var result = UsingsChangeDetector.ExtractUsings(source);
        Assert.Contains("global System", result);
    }

    [Fact]
    public void ExtractUsings_EmptyCode_ReturnsEmpty()
    {
        Assert.Empty(UsingsChangeDetector.ExtractUsings(""));
    }

    [Fact]
    public void ExtractUsings_NoUsings_ReturnsEmpty()
    {
        var source = "class C { void Run() { } }";
        Assert.Empty(UsingsChangeDetector.ExtractUsings(source));
    }

    [Fact]
    public void DetectChanges_AddedUsing_ReturnsAdded()
    {
        var before = new[] { "System" };
        var after = new[] { "System", "System.Linq" };
        var changes = UsingsChangeDetector.DetectChanges(before, after);

        var change = Assert.Single(changes);
        Assert.Equal(UsingChangeKind.Added, change.Kind);
        Assert.Equal("System.Linq", change.Namespace);
    }

    [Fact]
    public void DetectChanges_RemovedUsing_ReturnsRemoved()
    {
        var before = new[] { "System", "System.Linq" };
        var after = new[] { "System" };
        var changes = UsingsChangeDetector.DetectChanges(before, after);

        var change = Assert.Single(changes);
        Assert.Equal(UsingChangeKind.Removed, change.Kind);
        Assert.Equal("System.Linq", change.Namespace);
    }

    [Fact]
    public void DetectChanges_AddedAndRemoved_RemovedFirst()
    {
        var before = new[] { "Newtonsoft.Json" };
        var after = new[] { "System.Text.Json" };
        var changes = UsingsChangeDetector.DetectChanges(before, after);

        Assert.Equal(2, changes.Count);
        Assert.Equal(UsingChangeKind.Removed, changes[0].Kind);
        Assert.Equal("Newtonsoft.Json", changes[0].Namespace);
        Assert.Equal(UsingChangeKind.Added, changes[1].Kind);
        Assert.Equal("System.Text.Json", changes[1].Namespace);
    }

    [Fact]
    public void DetectChanges_NoChanges_ReturnsEmpty()
    {
        var usings = new[] { "System" };
        Assert.Empty(UsingsChangeDetector.DetectChanges(usings, usings));
    }

    [Fact]
    public void DetectChanges_BothEmpty_ReturnsEmpty()
    {
        Assert.Empty(UsingsChangeDetector.DetectChanges([], []));
    }
}
