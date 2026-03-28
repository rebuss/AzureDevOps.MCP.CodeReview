using Microsoft.Extensions.Logging.Abstractions;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Models.Pagination;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Services.Pagination;

namespace REBUSS.Pure.Tests.Services.Pagination;

public class PageAllocatorTests
{
    private readonly PageAllocator _allocator = new(NullLogger<PageAllocator>.Instance);

    // --- All items fit on 1 page ---

    [Fact]
    public void Allocate_AllItemsFit_ReturnsSinglePage()
    {
        var candidates = new List<PackingCandidate>
        {
            new("src/A.cs", 500, FileCategory.Source, 10),
            new("src/B.cs", 300, FileCategory.Source, 5)
        };

        var allocation = _allocator.Allocate(candidates, 2000);

        Assert.Equal(1, allocation.TotalPages);
        Assert.Equal(2, allocation.TotalItems);
        Assert.Equal(2, allocation.Pages[0].Items.Count);
        Assert.All(allocation.Pages[0].Items, i => Assert.Equal(PackingItemStatus.Included, i.Status));
    }

    // --- Items across 2 pages ---

    [Fact]
    public void Allocate_ItemsAcrossTwoPages_CorrectBoundaries()
    {
        var candidates = new List<PackingCandidate>
        {
            new("src/A.cs", 5000, FileCategory.Source, 20),
            new("src/B.cs", 3000, FileCategory.Source, 10),
            new("tests/C.cs", 4000, FileCategory.Test, 15)
        };

        // Budget: 8000 - 250 (base overhead) = 7750 available
        // A.cs: 5000 + 15 = 5015 → fits, 7750-5015=2735 remaining
        // B.cs: 3000 + 15 = 3015 → too big for 2735 → close page 1
        // Page 2: B.cs + C.cs
        var allocation = _allocator.Allocate(candidates, 8000);

        Assert.Equal(2, allocation.TotalPages);
        Assert.Equal(3, allocation.TotalItems);
        Assert.Equal(1, allocation.Pages[0].Items.Count);
        Assert.Equal(2, allocation.Pages[1].Items.Count);
    }

    // --- Items across 3+ pages ---

    [Fact]
    public void Allocate_ThreePlusPages_AllItemsAccountedFor()
    {
        var candidates = new List<PackingCandidate>
        {
            new("a.cs", 3000, FileCategory.Source, 10),
            new("b.cs", 3000, FileCategory.Source, 10),
            new("c.cs", 3000, FileCategory.Source, 10),
            new("d.cs", 3000, FileCategory.Source, 10)
        };

        // Budget: 4000 - 250 = 3750 available
        // Each item: 3000 + 15 = 3015, fits one per page
        // 4 items → 4 pages
        var allocation = _allocator.Allocate(candidates, 4000);

        Assert.True(allocation.TotalPages >= 2);
        Assert.Equal(4, allocation.TotalItems);

        // All items accounted for
        var totalItems = allocation.Pages.Sum(p => p.Items.Count);
        Assert.Equal(4, totalItems);
    }

    // --- Single oversized item ---

    [Fact]
    public void Allocate_OversizedItem_GetsOwnPageAsPartial()
    {
        var candidates = new List<PackingCandidate>
        {
            new("huge.cs", 100000, FileCategory.Source, 500),
            new("small.cs", 100, FileCategory.Source, 5)
        };

        var allocation = _allocator.Allocate(candidates, 5000);

        Assert.Equal(2, allocation.TotalPages);
        // First page: oversized item as Partial
        Assert.Single(allocation.Pages[0].Items);
        Assert.Equal(PackingItemStatus.Partial, allocation.Pages[0].Items[0].Status);
        Assert.NotNull(allocation.Pages[0].Items[0].BudgetForPartial);
        // Second page: small item
        Assert.Single(allocation.Pages[1].Items);
        Assert.Equal(PackingItemStatus.Included, allocation.Pages[1].Items[0].Status);
    }

    // --- Oversized in middle ---

    [Fact]
    public void Allocate_OversizedInMiddle_CorrectPageAssignment()
    {
        var candidates = new List<PackingCandidate>
        {
            new("small1.cs", 100, FileCategory.Source, 5),
            new("huge.cs", 100000, FileCategory.Source, 500),
            new("small2.cs", 100, FileCategory.Source, 5)
        };

        var allocation = _allocator.Allocate(candidates, 5000);

        Assert.True(allocation.TotalPages >= 2);
        Assert.Equal(3, allocation.TotalItems);
        var totalItemsOnPages = allocation.Pages.Sum(p => p.Items.Count);
        Assert.Equal(3, totalItemsOnPages);
    }

    // --- Empty candidates (Q21) ---

    [Fact]
    public void Allocate_EmptyCandidates_ReturnsSingleEmptyPage()
    {
        var candidates = new List<PackingCandidate>();

        var allocation = _allocator.Allocate(candidates, 5000);

        Assert.Equal(1, allocation.TotalPages);
        Assert.Equal(0, allocation.TotalItems);
        Assert.Single(allocation.Pages);
        Assert.Empty(allocation.Pages[0].Items);
        Assert.Equal(1, allocation.Pages[0].PageNumber);
    }

    // --- Budget exactly one item ---

    [Fact]
    public void Allocate_BudgetExactlyOneItem_SingleItemOnPage()
    {
        var candidates = new List<PackingCandidate>
        {
            new("a.cs", 500, FileCategory.Source, 10),
            new("b.cs", 500, FileCategory.Source, 10)
        };

        // Budget: 265 + 500 + 15 = 780 → just enough for 1 item + overhead
        var allocation = _allocator.Allocate(candidates, 780);

        Assert.True(allocation.TotalPages >= 1);
        Assert.Single(allocation.Pages[0].Items);
    }

    // --- All items oversized ---

    [Fact]
    public void Allocate_AllItemsOversized_EachGetsOwnPageAsPartial()
    {
        var candidates = new List<PackingCandidate>
        {
            new("a.cs", 100000, FileCategory.Source, 100),
            new("b.cs", 100000, FileCategory.Source, 100)
        };

        var allocation = _allocator.Allocate(candidates, 1000);

        Assert.Equal(2, allocation.TotalPages);
        Assert.All(allocation.Pages, p =>
        {
            Assert.Single(p.Items);
            Assert.Equal(PackingItemStatus.Partial, p.Items[0].Status);
        });
    }

    // --- Determinism ---

    [Fact]
    public void Allocate_SameInput_DeterministicOutput()
    {
        var candidates = new List<PackingCandidate>
        {
            new("src/A.cs", 5000, FileCategory.Source, 20),
            new("src/B.cs", 3000, FileCategory.Source, 10),
            new("tests/C.cs", 4000, FileCategory.Test, 15),
            new("docs/D.md", 2000, FileCategory.Docs, 5)
        };

        var first = _allocator.Allocate(candidates, 8000);

        for (var i = 0; i < 100; i++)
        {
            var result = _allocator.Allocate(candidates, 8000);
            Assert.Equal(first.TotalPages, result.TotalPages);
            Assert.Equal(first.TotalItems, result.TotalItems);
            for (var p = 0; p < first.Pages.Count; p++)
            {
                Assert.Equal(first.Pages[p].Items.Count, result.Pages[p].Items.Count);
                Assert.Equal(first.Pages[p].StartIndex, result.Pages[p].StartIndex);
                Assert.Equal(first.Pages[p].EndIndex, result.Pages[p].EndIndex);
            }
        }
    }

    // --- Per-item manifest overhead correctly deducted ---

    [Fact]
    public void Allocate_PerItemOverheadDeducted_AffectsPageBoundaries()
    {
        // If per-item overhead is 15, then with tight budget, fewer items fit
        var candidates = new List<PackingCandidate>
        {
            new("a.cs", 100, FileCategory.Source, 10),
            new("b.cs", 100, FileCategory.Source, 10),
            new("c.cs", 100, FileCategory.Source, 10)
        };

        // Budget: 250 (base) + 3 * (100 + 15) = 250 + 345 = 595
        // At 595 budget: available = 595 - 250 = 345, each item = 115, fits 3 items
        var allocation = _allocator.Allocate(candidates, 595);
        Assert.Equal(1, allocation.TotalPages);
        Assert.Equal(3, allocation.Pages[0].Items.Count);

        // At 594: available = 594 - 250 = 344, each item = 115, fits 2 items (2*115=230 < 344, 3*115=345 > 344)
        var allocation2 = _allocator.Allocate(candidates, 594);
        Assert.True(allocation2.TotalPages >= 2 || allocation2.Pages[0].Items.Count <= 2);
    }

    // --- 50+ items scale test ---

    [Fact]
    public void Allocate_FiftyPlusItems_NoItemsLost()
    {
        var candidates = new List<PackingCandidate>();
        for (var i = 0; i < 60; i++)
        {
            candidates.Add(new PackingCandidate($"file{i}.cs", 500, FileCategory.Source, i + 1));
        }

        var allocation = _allocator.Allocate(candidates, 5000);

        Assert.True(allocation.TotalPages > 1);
        Assert.Equal(60, allocation.TotalItems);

        // No items lost across pages
        var totalItemsOnPages = allocation.Pages.Sum(p => p.Items.Count);
        Assert.Equal(60, totalItemsOnPages);

        // No duplicate indices
        var allIndices = allocation.Pages.SelectMany(p => p.Items.Select(i => i.OriginalIndex)).ToList();
        Assert.Equal(60, allIndices.Distinct().Count());
    }

    // --- Budget too small ---

    [Fact]
    public void Allocate_BudgetTooSmall_ThrowsInvalidOperation()
    {
        var candidates = new List<PackingCandidate>
        {
            new("a.cs", 100, FileCategory.Source, 10)
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => _allocator.Allocate(candidates, 200));
        Assert.Contains("too small", ex.Message);
    }

    // --- Page numbers are sequential ---

    [Fact]
    public void Allocate_PageNumbers_AreSequentialStartingFromOne()
    {
        var candidates = new List<PackingCandidate>
        {
            new("a.cs", 3000, FileCategory.Source, 10),
            new("b.cs", 3000, FileCategory.Source, 10),
            new("c.cs", 3000, FileCategory.Source, 10)
        };

        var allocation = _allocator.Allocate(candidates, 4000);

        for (var i = 0; i < allocation.Pages.Count; i++)
        {
            Assert.Equal(i + 1, allocation.Pages[i].PageNumber);
        }
    }
}
