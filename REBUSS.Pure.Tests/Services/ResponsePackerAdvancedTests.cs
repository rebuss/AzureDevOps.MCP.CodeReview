using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Services.ResponsePacking;

namespace REBUSS.Pure.Tests.Services.ResponsePacking;

/// <summary>
/// Tests for manifest completeness, edge cases, JSON structure, budget compliance,
/// and backward compatibility (T029–T033).
/// </summary>
public class ResponsePackerAdvancedTests
{
    private readonly ResponsePacker _packer = new(NullLogger<ResponsePacker>.Instance);

    // --- T029: Manifest completeness ---

    [Fact]
    public void Pack_ManifestItemCount_MatchesCandidateCount()
    {
        var candidates = new List<PackingCandidate>
        {
            new("a.cs", 100, FileCategory.Source, 10),
            new("b.cs", 200, FileCategory.Test, 5),
            new("c.md", 50, FileCategory.Docs, 2),
        };

        var decision = _packer.Pack(candidates, 10000);

        Assert.Equal(candidates.Count, decision.Manifest.Items.Count);
    }

    [Fact]
    public void Pack_ManifestSummary_CountsMatchItemStatuses()
    {
        var candidates = new List<PackingCandidate>
        {
            new("a.cs", 300, FileCategory.Source, 10),
            new("b.cs", 300, FileCategory.Test, 5),
            new("c.cs", 300, FileCategory.Docs, 2),
        };

        // Budget: overhead=100+3*15=145, effective=555. a(300) fits. b(300)>255→partial. c→deferred.
        var decision = _packer.Pack(candidates, 700);
        var summary = decision.Manifest.Summary;

        var actualIncluded = decision.Manifest.Items.Count(i => i.Status == PackingItemStatus.Included);
        var actualPartial = decision.Manifest.Items.Count(i => i.Status == PackingItemStatus.Partial);
        var actualDeferred = decision.Manifest.Items.Count(i => i.Status == PackingItemStatus.Deferred);

        Assert.Equal(actualIncluded, summary.IncludedCount);
        Assert.Equal(actualPartial, summary.PartialCount);
        Assert.Equal(actualDeferred, summary.DeferredCount);
        Assert.Equal(candidates.Count, summary.TotalItems);
    }

    [Fact]
    public void Pack_ManifestItems_PathsMatchCandidates()
    {
        var candidates = new List<PackingCandidate>
        {
            new("first.cs", 100, FileCategory.Source, 10),
            new("second.cs", 100, FileCategory.Test, 5),
        };

        var decision = _packer.Pack(candidates, 10000);

        for (var i = 0; i < candidates.Count; i++)
        {
            Assert.Equal(candidates[i].Path, decision.Manifest.Items[i].Path);
        }
    }

    // --- T030: Edge cases ---

    [Fact]
    public void Pack_SingleCandidate_ThatFits_IsIncluded()
    {
        var candidates = new List<PackingCandidate>
        {
            new("only.cs", 50, FileCategory.Source, 10),
        };

        var decision = _packer.Pack(candidates, 10000);

        Assert.Single(decision.Items);
        Assert.Equal(PackingItemStatus.Included, decision.Items[0].Status);
    }

    [Fact]
    public void Pack_SingleCandidate_ThatExceedsBudget_IsPartial()
    {
        var candidates = new List<PackingCandidate>
        {
            new("huge.cs", 50000, FileCategory.Source, 100),
        };

        // Very small budget
        var decision = _packer.Pack(candidates, 200);

        Assert.Single(decision.Items);
        Assert.Equal(PackingItemStatus.Partial, decision.Items[0].Status);
    }

    [Fact]
    public void Pack_ManySmallItems_AllFit()
    {
        var candidates = Enumerable.Range(0, 100)
            .Select(i => new PackingCandidate($"file{i}.cs", 10, FileCategory.Source, 1))
            .ToList();

        // Budget should handle 100 items × 10 tokens + overhead (100+100×15=1600). Need 2600 total.
        var decision = _packer.Pack(candidates, 5000);

        Assert.All(decision.Items, item => Assert.Equal(PackingItemStatus.Included, item.Status));
    }

    [Fact]
    public void Pack_AllCandidatesExceedBudget_FirstIsPartial_RestDeferred()
    {
        var candidates = new List<PackingCandidate>
        {
            new("a.cs", 5000, FileCategory.Source, 10),
            new("b.cs", 5000, FileCategory.Test, 5),
            new("c.cs", 5000, FileCategory.Docs, 2),
        };

        var decision = _packer.Pack(candidates, 200);

        var statuses = decision.Items.Select(i => i.Status).ToList();
        Assert.Equal(1, statuses.Count(s => s == PackingItemStatus.Partial));
        Assert.Equal(2, statuses.Count(s => s == PackingItemStatus.Deferred));
    }

    // --- T031: JSON serialization of manifest ---

    [Fact]
    public void Pack_ManifestEntryStatus_IsStringEnumName()
    {
        var candidates = new List<PackingCandidate>
        {
            new("a.cs", 100, FileCategory.Source, 10),
        };

        var decision = _packer.Pack(candidates, 10000);
        var entry = decision.Manifest.Items[0];

        Assert.Equal("Included", entry.Status.ToString());
    }

    [Fact]
    public void Pack_ManifestUtilization_IsRoundedToOneDecimal()
    {
        var candidates = new List<PackingCandidate>
        {
            new("a.cs", 333, FileCategory.Source, 10),
        };

        var decision = _packer.Pack(candidates, 1000);

        // Utilization = 333/1000 * 100 = 33.3
        Assert.Equal(33.3, decision.Manifest.Summary.UtilizationPercent);
    }

    // --- T032: Budget compliance ---

    [Fact]
    public void Pack_IncludedItems_NeverExceedBudget()
    {
        var candidates = new List<PackingCandidate>
        {
            new("a.cs", 400, FileCategory.Source, 20),
            new("b.cs", 300, FileCategory.Source, 15),
            new("c.cs", 200, FileCategory.Test, 10),
            new("d.cs", 100, FileCategory.Config, 5),
        };

        var budget = 1200;
        var decision = _packer.Pack(candidates, budget);

        var includedTokens = decision.Items
            .Where(i => i.Status == PackingItemStatus.Included)
            .Sum(i => i.EstimatedTokens);

        Assert.True(includedTokens <= budget);
    }

    [Fact]
    public void Pack_ManifestOverhead_IsAccountedFor()
    {
        // 3 items: overhead = 100 + 3*15 = 145
        var candidates = new List<PackingCandidate>
        {
            new("a.cs", 300, FileCategory.Source, 10),
            new("b.cs", 300, FileCategory.Source, 5),
            new("c.cs", 300, FileCategory.Source, 3),
        };

        // Budget = 600. Effective = 600 - 145 = 455. Only a(300) fits. b(300)>155→partial.
        var decision = _packer.Pack(candidates, 600);

        var included = decision.Items.Count(i => i.Status == PackingItemStatus.Included);
        Assert.Equal(1, included);
    }

    // --- T033: Backward compatibility ---

    [Fact]
    public void Pack_DecisionItems_PreserveOriginalIndices()
    {
        var candidates = new List<PackingCandidate>
        {
            new("z.cs", 100, FileCategory.Test, 5),
            new("a.cs", 100, FileCategory.Source, 10),
        };

        var decision = _packer.Pack(candidates, 10000);

        // Items should be indexed by original order
        Assert.Equal(2, decision.Items.Count);
        for (var i = 0; i < decision.Items.Count; i++)
        {
            Assert.Equal(i, decision.Items[i].OriginalIndex);
        }
    }

    [Fact]
    public void Pack_WithGenerousBudget_AllItemsIncluded_NoDeferrals()
    {
        var candidates = new List<PackingCandidate>
        {
            new("a.cs", 100, FileCategory.Source, 10),
            new("b.cs", 200, FileCategory.Test, 5),
            new("c.md", 50, FileCategory.Docs, 2),
        };

        var decision = _packer.Pack(candidates, 200000);

        Assert.Equal(0, decision.Manifest.Summary.DeferredCount);
        Assert.Equal(0, decision.Manifest.Summary.PartialCount);
        Assert.Equal(3, decision.Manifest.Summary.IncludedCount);
    }
}
