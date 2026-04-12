using Microsoft.Extensions.Logging.Abstractions;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Services.ResponsePacking;

namespace REBUSS.Pure.Tests.Services.ResponsePacking;

public class ResponsePackerTests
{
    private readonly ResponsePacker _packer = new(NullLogger<ResponsePacker>.Instance);

    // --- Empty candidates ---

    [Fact]
    public void Pack_EmptyCandidates_ReturnsEmptyDecision()
    {
        var decision = _packer.Pack(Array.Empty<PackingCandidate>(), 10000);

        Assert.Empty(decision.Items);
        Assert.Equal(0, decision.Manifest.Summary.TotalItems);
        Assert.Equal(10000, decision.Manifest.Summary.TotalBudgetTokens);
        Assert.Equal(0, decision.Manifest.Summary.BudgetUsed);
    }

    // --- All fit ---

    [Fact]
    public void Pack_AllFit_AllIncluded()
    {
        var candidates = new List<PackingCandidate>
        {
            new("a.cs", 100, FileCategory.Source, 10),
            new("b.cs", 200, FileCategory.Source, 5),
            new("c.cs", 50, FileCategory.Test, 3),
        };

        var decision = _packer.Pack(candidates, 10000);

        Assert.Equal(3, decision.Items.Count);
        Assert.All(decision.Items, item => Assert.Equal(PackingItemStatus.Included, item.Status));
        Assert.Equal(3, decision.Manifest.Summary.IncludedCount);
        Assert.Equal(0, decision.Manifest.Summary.PartialCount);
        Assert.Equal(0, decision.Manifest.Summary.DeferredCount);
    }

    // --- Budget exceeded — deferred items ---

    [Fact]
    public void Pack_BudgetExceeded_HighPriorityIncluded_LowPriorityDeferred()
    {
        var candidates = new List<PackingCandidate>
        {
            new("src/Service.cs", 500, FileCategory.Source, 20),
            new("tests/ServiceTests.cs", 300, FileCategory.Test, 10),
            new("docs/readme.md", 400, FileCategory.Docs, 5),
        };

        // Budget enough for ~700 tokens after overhead (100 + 3*15 = 145 overhead)
        // Effective = 855 - 145 = ~710. Source(500) fits, Test(300) would push to 800 > 710
        var decision = _packer.Pack(candidates, 855);

        // Source should be included (500 ≤ 710)
        Assert.Equal(PackingItemStatus.Included, decision.Items[0].Status);
        // Test should be partial (first oversized) or deferred depending on remaining budget
        // Remaining after source = 710 - 500 = 210. Test(300) > 210 → Partial with budget 210
        Assert.Equal(PackingItemStatus.Partial, decision.Items[1].Status);
        Assert.Equal(210, decision.Items[1].BudgetForPartial);
        // Docs should be deferred (partial already assigned)
        Assert.Equal(PackingItemStatus.Deferred, decision.Items[2].Status);
    }

    // --- Partial assignment ---

    [Fact]
    public void Pack_FirstOversizedItem_GetsPartialStatus()
    {
        var candidates = new List<PackingCandidate>
        {
            new("big.cs", 5000, FileCategory.Source, 100),
        };

        // Budget of 1000, overhead = 100 + 1*15 = 115, effective = 885
        // Item 5000 > 885 → partial with budget 885
        var decision = _packer.Pack(candidates, 1000);

        Assert.Single(decision.Items);
        Assert.Equal(PackingItemStatus.Partial, decision.Items[0].Status);
        Assert.Equal(885, decision.Items[0].BudgetForPartial);
    }

    [Fact]
    public void Pack_OnlyOnePartialAllowed()
    {
        var candidates = new List<PackingCandidate>
        {
            new("big1.cs", 5000, FileCategory.Source, 100),
            new("big2.cs", 5000, FileCategory.Source, 50),
        };

        // Budget of 1000, overhead = 100 + 2*15 = 130, effective = 870
        var decision = _packer.Pack(candidates, 1000);

        var partial = decision.Items.Count(i => i.Status == PackingItemStatus.Partial);
        var deferred = decision.Items.Count(i => i.Status == PackingItemStatus.Deferred);

        Assert.Equal(1, partial);
        Assert.Equal(1, deferred);
    }

    // --- Priority ordering ---

    [Fact]
    public void Pack_SourceIncludedBeforeTest_WhenBudgetLimited()
    {
        var candidates = new List<PackingCandidate>
        {
            new("test.cs", 400, FileCategory.Test, 10),
            new("source.cs", 400, FileCategory.Source, 10),
        };

        // Budget just enough for one: overhead = 100 + 2*15 = 130, effective = 570
        // Source (400) fits, Test (400) > 170 remaining → partial
        var decision = _packer.Pack(candidates, 700);

        // Source (index 1 in original list) should be Included
        Assert.Equal(PackingItemStatus.Included, decision.Items[1].Status);
        // Test (index 0 in original list) should be Partial or Deferred
        Assert.NotEqual(PackingItemStatus.Included, decision.Items[0].Status);
    }

    // --- Manifest correctness ---

    [Fact]
    public void Pack_ManifestSummary_MatchesDecisionItems()
    {
        var candidates = new List<PackingCandidate>
        {
            new("a.cs", 100, FileCategory.Source, 10),
            new("b.cs", 200, FileCategory.Test, 5),
        };

        var decision = _packer.Pack(candidates, 10000);

        Assert.Equal(2, decision.Manifest.Summary.TotalItems);
        Assert.Equal(2, decision.Manifest.Summary.IncludedCount);
        Assert.Equal(10000, decision.Manifest.Summary.TotalBudgetTokens);
        Assert.Equal(300, decision.Manifest.Summary.BudgetUsed);
        Assert.Equal(9700, decision.Manifest.Summary.BudgetRemaining);
    }

    [Fact]
    public void Pack_ManifestItems_PreserveOriginalOrder()
    {
        var candidates = new List<PackingCandidate>
        {
            new("z_test.cs", 100, FileCategory.Test, 5),
            new("a_source.cs", 100, FileCategory.Source, 10),
        };

        var decision = _packer.Pack(candidates, 10000);

        // Manifest should preserve original order (not sorted order)
        Assert.Equal("z_test.cs", decision.Manifest.Items[0].Path);
        Assert.Equal("a_source.cs", decision.Manifest.Items[1].Path);
    }

    [Fact]
    public void Pack_ManifestEntries_HaveCorrectPriorityTier()
    {
        var candidates = new List<PackingCandidate>
        {
            new("service.cs", 100, FileCategory.Source, 10),
            new("readme.md", 100, FileCategory.Docs, 5),
        };

        var decision = _packer.Pack(candidates, 10000);

        Assert.Equal("Source", decision.Manifest.Items[0].PriorityTier);
        Assert.Equal("Docs", decision.Manifest.Items[1].PriorityTier);
    }

    // --- Utilization ---

    [Fact]
    public void Pack_UtilizationPercent_CalculatedCorrectly()
    {
        var candidates = new List<PackingCandidate>
        {
            new("a.cs", 5000, FileCategory.Source, 10),
        };

        var decision = _packer.Pack(candidates, 10000);

        Assert.Equal(50.0, decision.Manifest.Summary.UtilizationPercent);
    }

    // --- Zero budget ---

    [Fact]
    public void Pack_ZeroBudget_AllDeferred()
    {
        var candidates = new List<PackingCandidate>
        {
            new("a.cs", 100, FileCategory.Source, 10),
        };

        var decision = _packer.Pack(candidates, 0);

        // With 0 budget, nothing fits fully. Items are either Deferred or Partial (truncated to 0).
        Assert.All(decision.Items, item =>
            Assert.True(item.Status == PackingItemStatus.Deferred || item.Status == PackingItemStatus.Partial));
    }
}
