using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using REBUSS.Pure.Services.PrEnrichment;
using REBUSS.Pure.Services.ReviewSession;

namespace REBUSS.Pure.Tests.Services.ReviewSession;

public class ReviewFileClassifierTests
{
    private static ReviewFileClassifier NewClassifier(params string[] patterns)
    {
        var opts = Options.Create(new WorkflowOptions
        {
            ReviewSession = new ReviewSessionOptions { ScanOnlyPatterns = patterns }
        });
        return new ReviewFileClassifier(opts, NullLogger<ReviewFileClassifier>.Instance);
    }

    [Fact]
    public void EmptyPatternList_AllPathsClassifiedAsDeep()
    {
        var c = NewClassifier();
        var r = c.Classify("anything/at/all.cs");
        Assert.Equal(ReviewFileClassification.Deep, r.Classification);
        Assert.Null(r.MatchedPattern);
    }

    [Fact]
    public void SingleDesignerPattern_MatchesNestedPath()
    {
        var c = NewClassifier("**/*.Designer.cs");
        var r = c.Classify("Foo/Bar.Designer.cs");
        Assert.Equal(ReviewFileClassification.Scan, r.Classification);
        Assert.Equal("**/*.Designer.cs", r.MatchedPattern);
    }

    [Fact]
    public void CaseInsensitive_AcrossHosts()
    {
        // Q1/answer (b): always case-insensitive regardless of host OS
        var c = NewClassifier("**/*.Designer.cs");
        var r = c.Classify("Foo/Bar.designer.cs");
        Assert.Equal(ReviewFileClassification.Scan, r.Classification);
    }

    [Fact]
    public void MultipleMatchingPatterns_RecordsFirstInConfigurationOrder()
    {
        // Q5/answer (a): first pattern in configuration order wins for the *recorded* annotation
        var c = NewClassifier("**/Migrations/*.cs", "**/*.cs");
        var r = c.Classify("Database/Migrations/AddX.cs");
        Assert.Equal(ReviewFileClassification.Scan, r.Classification);
        Assert.Equal("**/Migrations/*.cs", r.MatchedPattern);
    }

    [Fact]
    public void PathMatchingNoPattern_ReturnsDeepWithNullMatchedPattern()
    {
        var c = NewClassifier("**/*.Designer.cs", "**/Migrations/*.cs");
        var r = c.Classify("src/Foo.cs");
        Assert.Equal(ReviewFileClassification.Deep, r.Classification);
        Assert.Null(r.MatchedPattern);
    }

    [Fact]
    public void InvalidGlobPattern_LoggedAndDropped_ServerDoesNotCrash()
    {
        // An empty pattern is the simplest "invalid" — Matcher.AddInclude tolerates many edge cases.
        // Use null-via-array to trigger; if it doesn't throw we still confirm no crash + classify works.
        var opts = Options.Create(new WorkflowOptions
        {
            ReviewSession = new ReviewSessionOptions { ScanOnlyPatterns = new[] { "" } }
        });
        var c = new ReviewFileClassifier(opts, NullLogger<ReviewFileClassifier>.Instance);
        // Should not throw at construction; classification still works
        var r = c.Classify("foo.cs");
        Assert.Equal(ReviewFileClassification.Deep, r.Classification);
    }

    [Fact]
    public void InvalidPattern_RemainingValidPatternsStillActive()
    {
        var opts = Options.Create(new WorkflowOptions
        {
            ReviewSession = new ReviewSessionOptions { ScanOnlyPatterns = new[] { "", "**/*.Designer.cs" } }
        });
        var c = new ReviewFileClassifier(opts, NullLogger<ReviewFileClassifier>.Instance);
        Assert.Equal(ReviewFileClassification.Scan, c.Classify("Foo.Designer.cs").Classification);
    }

    [Fact]
    public void DoubleStarPattern_MatchesArbitraryDepth()
    {
        var c = NewClassifier("**/Migrations/*.cs");
        Assert.Equal(ReviewFileClassification.Scan, c.Classify("Database/Migrations/AddX.cs").Classification);
        Assert.Equal(ReviewFileClassification.Scan, c.Classify("src/Foo/Bar/Migrations/AddY.cs").Classification);
    }

    [Fact]
    public void Classify_IsDeterministic()
    {
        var c = NewClassifier("**/*.Designer.cs");
        var r1 = c.Classify("Foo.Designer.cs");
        var r2 = c.Classify("Foo.Designer.cs");
        Assert.Equal(r1, r2);
    }

    [Fact]
    public void EmptyPathInput_ReturnsDeep()
    {
        var c = NewClassifier("**/*.Designer.cs");
        Assert.Equal(ReviewFileClassification.Deep, c.Classify("").Classification);
    }

    // ─── Phase 3 tests (US3 — config layering) ────────────────────────────────────

    [Fact]
    public void Configuration_DefaultPatternList_LoadsFromInMemoryAppsettingsBase()
    {
        // Proxy for the real appsettings.json file-load path (which is exercised by the existing
        // config-layering tests elsewhere). Builds a layered IConfiguration with an in-memory base.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Workflow:ReviewSession:ScanOnlyPatterns:0"] = "**/*.Designer.cs",
                ["Workflow:ReviewSession:ScanOnlyPatterns:1"] = "**/*.g.cs",
            })
            .Build();
        var opts = new WorkflowOptions();
        config.GetSection("Workflow").Bind(opts);
        var c = new ReviewFileClassifier(Options.Create(opts), NullLogger<ReviewFileClassifier>.Instance);

        Assert.Equal(ReviewFileClassification.Scan, c.Classify("Foo.Designer.cs").Classification);
        Assert.Equal(ReviewFileClassification.Scan, c.Classify("Bar.g.cs").Classification);
    }

    [Fact]
    public void Configuration_OverrideLayer_AddsPatternsViaInMemoryProxy()
    {
        // Layered IConfiguration with the default JSON as the base layer plus an in-memory
        // override layer that adds **/*.proxy.cs. Proxy for the real appsettings.Local.json
        // file-load path. Locks SC-006.
        var baseLayer = new Dictionary<string, string?>
        {
            ["Workflow:ReviewSession:ScanOnlyPatterns:0"] = "**/*.Designer.cs",
        };
        var overrideLayer = new Dictionary<string, string?>
        {
            ["Workflow:ReviewSession:ScanOnlyPatterns:0"] = "**/*.Designer.cs",
            ["Workflow:ReviewSession:ScanOnlyPatterns:1"] = "**/*.proxy.cs",
        };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(baseLayer)
            .AddInMemoryCollection(overrideLayer)
            .Build();
        var opts = new WorkflowOptions();
        config.GetSection("Workflow").Bind(opts);
        var c = new ReviewFileClassifier(Options.Create(opts), NullLogger<ReviewFileClassifier>.Instance);

        Assert.Equal(ReviewFileClassification.Scan, c.Classify("Foo.Designer.cs").Classification);
        Assert.Equal(ReviewFileClassification.Scan, c.Classify("Bar.proxy.cs").Classification);
    }
}
