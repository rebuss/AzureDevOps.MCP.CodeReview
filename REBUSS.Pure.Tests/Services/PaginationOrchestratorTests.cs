using System.Text.Json;
using NSubstitute;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models.Pagination;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Services.Pagination;
using REBUSS.Pure.Tools.Models;

namespace REBUSS.Pure.Tests.Services.Pagination;

public class PaginationOrchestratorTests
{
    // --- ValidateInputs ---

    [Fact]
    public void ValidateInputs_BothNull_ReturnsNull()
    {
        Assert.Null(PaginationOrchestrator.ValidateInputs(null, null));
    }

    [Fact]
    public void ValidateInputs_OnlyPageReference_ReturnsNull()
    {
        Assert.Null(PaginationOrchestrator.ValidateInputs("some_ref", null));
    }

    [Fact]
    public void ValidateInputs_OnlyPageNumber_ReturnsNull()
    {
        Assert.Null(PaginationOrchestrator.ValidateInputs(null, 2));
    }

    [Fact]
    public void ValidateInputs_Both_ReturnsError()
    {
        var error = PaginationOrchestrator.ValidateInputs("ref", 2);
        Assert.NotNull(error);
        Assert.Contains("Cannot specify both", error);
    }

    // --- ResolvePage ---

    [Fact]
    public void ResolvePage_NoReferenceNoPage_DefaultsToPage1()
    {
        var codec = Substitute.For<IPageReferenceCodec>();
        var result = PaginationOrchestrator.ResolvePage(null, null, codec, 50000, false);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.PageNumber);
        Assert.Equal(50000, result.ResolvedBudget);
    }

    [Fact]
    public void ResolvePage_WithPageNumber_ReturnsRequestedPage()
    {
        var codec = Substitute.For<IPageReferenceCodec>();
        var result = PaginationOrchestrator.ResolvePage(null, 3, codec, 50000, true);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.PageNumber);
        Assert.Null(result.Fingerprint);
    }

    [Fact]
    public void ResolvePage_WithValidPageReference_DecodesSuccessfully()
    {
        var codec = Substitute.For<IPageReferenceCodec>();
        var requestParams = JsonDocument.Parse("{\"prNumber\":42}").RootElement;
        codec.TryDecode("valid").Returns(new PageReferenceData("get_pr_files", requestParams, 89600, 2, "sha123"));

        var result = PaginationOrchestrator.ResolvePage("valid", null, codec, 89600, false);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.PageNumber);
        Assert.Equal("sha123", result.Fingerprint);
        Assert.Equal(89600, result.ResolvedBudget); // Budget from reference
    }

    [Fact]
    public void ResolvePage_InvalidPageReference_ReturnsGenericError()
    {
        var codec = Substitute.For<IPageReferenceCodec>();
        codec.TryDecode("bad").Returns((PageReferenceData?)null);

        var result = PaginationOrchestrator.ResolvePage("bad", null, codec, 50000, false);

        Assert.False(result.IsSuccess);
        Assert.Contains("malformed", result.ErrorMessage);
    }

    [Fact]
    public void ResolvePage_BudgetMismatch_WithExplicitBudget_ReturnsError()
    {
        var codec = Substitute.For<IPageReferenceCodec>();
        var requestParams = JsonDocument.Parse("{\"prNumber\":42}").RootElement;
        codec.TryDecode("ref").Returns(new PageReferenceData("get_pr_files", requestParams, 89600, 2, "sha"));

        var result = PaginationOrchestrator.ResolvePage("ref", null, codec, 50000, true);

        Assert.False(result.IsSuccess);
        Assert.Contains("Budget mismatch", result.ErrorMessage);
    }

    [Fact]
    public void ResolvePage_BudgetMismatch_WithoutExplicitBudget_UsesReferenceBudget()
    {
        var codec = Substitute.For<IPageReferenceCodec>();
        var requestParams = JsonDocument.Parse("{\"prNumber\":42}").RootElement;
        codec.TryDecode("ref").Returns(new PageReferenceData("get_pr_files", requestParams, 89600, 2, "sha"));

        // When no explicit budget (false), reference budget is authoritative (Q18)
        var result = PaginationOrchestrator.ResolvePage("ref", null, codec, 50000, false);

        Assert.True(result.IsSuccess);
        Assert.Equal(89600, result.ResolvedBudget); // Uses reference budget, not resolved
    }

    // --- ValidateParameterMatch ---

    [Fact]
    public void ValidateParameterMatch_MatchingValues_ReturnsNull()
    {
        var decoded = JsonDocument.Parse("{\"prNumber\":42}").RootElement;
        var provided = JsonDocument.Parse("42").RootElement;

        Assert.Null(PaginationOrchestrator.ValidateParameterMatch(decoded, "prNumber", provided));
    }

    [Fact]
    public void ValidateParameterMatch_DifferentValues_ReturnsError()
    {
        var decoded = JsonDocument.Parse("{\"prNumber\":42}").RootElement;
        var provided = JsonDocument.Parse("99").RootElement;

        var error = PaginationOrchestrator.ValidateParameterMatch(decoded, "prNumber", provided);
        Assert.NotNull(error);
        Assert.Contains("mismatch", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateParameterMatch_NullDecoded_ReturnsNull()
    {
        var provided = JsonDocument.Parse("42").RootElement;
        Assert.Null(PaginationOrchestrator.ValidateParameterMatch(null, "prNumber", provided));
    }

    [Fact]
    public void ValidateParameterMatch_NullProvided_ReturnsNull()
    {
        var decoded = JsonDocument.Parse("{\"prNumber\":42}").RootElement;
        Assert.Null(PaginationOrchestrator.ValidateParameterMatch(decoded, "prNumber", null));
    }

    [Fact]
    public void ValidateParameterMatch_FieldNotInDecoded_ReturnsNull()
    {
        var decoded = JsonDocument.Parse("{\"scope\":\"staged\"}").RootElement;
        var provided = JsonDocument.Parse("42").RootElement;

        Assert.Null(PaginationOrchestrator.ValidateParameterMatch(decoded, "prNumber", provided));
    }

    // --- CheckStaleness ---

    [Fact]
    public void CheckStaleness_SameFingerprint_ReturnsNull()
    {
        Assert.Null(PaginationOrchestrator.CheckStaleness("sha", "sha", true));
    }

    [Fact]
    public void CheckStaleness_DifferentFingerprint_ReturnsWarning()
    {
        var result = PaginationOrchestrator.CheckStaleness("old_sha", "new_sha", true);
        Assert.NotNull(result);
        Assert.Equal("old_sha", result.OriginalFingerprint);
        Assert.Equal("new_sha", result.CurrentFingerprint);
        Assert.Contains("changed", result.Message);
    }

    [Fact]
    public void CheckStaleness_NotPageRefMode_ReturnsNull()
    {
        // Q20: Staleness does NOT apply to pageNumber mode
        Assert.Null(PaginationOrchestrator.CheckStaleness("old", "new", false));
    }

    [Fact]
    public void CheckStaleness_NullOriginal_ReturnsNull()
    {
        Assert.Null(PaginationOrchestrator.CheckStaleness(null, "sha", true));
    }

    [Fact]
    public void CheckStaleness_NullCurrent_ReturnsNull()
    {
        Assert.Null(PaginationOrchestrator.CheckStaleness("sha", null, true));
    }

    // --- BuildPaginationMetadata ---

    [Fact]
    public void BuildPaginationMetadata_SinglePage_NoNext()
    {
        var codec = Substitute.For<IPageReferenceCodec>();
        codec.Encode(Arg.Any<PageReferenceData>()).Returns("encoded");

        var page = new PageSlice(1, 0, 3,
            new[] { new PageSliceItem(0, PackingItemStatus.Included, 100) },
            100, 900);
        var allocation = new PageAllocation(new[] { page }, 1, 3);

        var requestParams = JsonDocument.Parse("{\"prNumber\":42}").RootElement;
        var meta = PaginationOrchestrator.BuildPaginationMetadata(
            allocation, 1, codec, "get_pr_files", requestParams, 1000, "sha");

        Assert.Equal(1, meta.CurrentPage);
        Assert.Equal(1, meta.TotalPages);
        Assert.False(meta.HasMore);
        Assert.Null(meta.NextPageReference);
        Assert.Equal("encoded", meta.CurrentPageReference);
    }

    [Fact]
    public void BuildPaginationMetadata_Page1Of3_HasNext()
    {
        var codec = Substitute.For<IPageReferenceCodec>();
        codec.Encode(Arg.Any<PageReferenceData>()).Returns("encoded");

        var pages = new[]
        {
            new PageSlice(1, 0, 1, Array.Empty<PageSliceItem>(), 0, 0),
            new PageSlice(2, 1, 2, Array.Empty<PageSliceItem>(), 0, 0),
            new PageSlice(3, 2, 3, Array.Empty<PageSliceItem>(), 0, 0)
        };
        var allocation = new PageAllocation(pages, 3, 3);

        var requestParams = JsonDocument.Parse("{\"prNumber\":42}").RootElement;
        var meta = PaginationOrchestrator.BuildPaginationMetadata(
            allocation, 1, codec, "get_pr_files", requestParams, 1000, "sha");

        Assert.Equal(1, meta.CurrentPage);
        Assert.Equal(3, meta.TotalPages);
        Assert.True(meta.HasMore);
        Assert.NotNull(meta.NextPageReference);
    }

    // --- BuildExtendedManifestSummary ---

    [Fact]
    public void BuildExtendedManifestSummary_HasPaginationFields()
    {
        var page1 = new PageSlice(1, 0, 2,
            new[]
            {
                new PageSliceItem(0, PackingItemStatus.Included, 500),
                new PageSliceItem(1, PackingItemStatus.Included, 300)
            }, 800, 200);
        var page2 = new PageSlice(2, 2, 4,
            new[]
            {
                new PageSliceItem(2, PackingItemStatus.Included, 400),
                new PageSliceItem(3, PackingItemStatus.Included, 200)
            }, 600, 400);
        var allocation = new PageAllocation(new[] { page1, page2 }, 2, 4);

        var summary = PaginationOrchestrator.BuildExtendedManifestSummary(page1, allocation, 5000);

        Assert.Equal(4, summary.TotalItems);
        Assert.Equal(2, summary.IncludedOnThisPage);
        Assert.Equal(2, summary.RemainingAfterThisPage);
        Assert.Equal(2, summary.TotalPages);
    }
}
