using System.Text.Json;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models.Pagination;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Tools.Models;

namespace REBUSS.Pure.Services.Pagination;

/// <summary>
/// Stateless orchestration helper for pagination logic shared across tool handlers.
/// Coordinates IPageAllocator and IPageReferenceCodec calls.
/// Not a DI service — instantiated inline by handlers.
/// </summary>
internal static class PaginationOrchestrator
{
    // --- Input Validation ---

    /// <summary>
    /// Validates mutual exclusion of pageReference and pageNumber.
    /// Returns error message or null if valid.
    /// </summary>
    public static string? ValidateInputs(string? pageReference, int? pageNumber)
    {
        if (pageReference != null && pageNumber != null)
        {
            return "Cannot specify both 'pageReference' and 'pageNumber'. " +
                   "Use 'pageReference' for sequential/resume access, or 'pageNumber' with original request parameters for direct access.";
        }

        return null;
    }

    // --- Page Resolution ---

    /// <summary>
    /// Resolves which page to serve and extracts context from pageReference if present.
    /// </summary>
    public static PageResolutionResult ResolvePage(
        string? pageReference,
        int? pageNumber,
        IPageReferenceCodec codec,
        int resolvedBudget,
        bool explicitBudgetProvided)
    {
        if (pageReference != null)
        {
            var refData = codec.TryDecode(pageReference);
            if (refData == null)
            {
                // Per Q23/FR-011: generic error only — never expose decoded internals
                return PageResolutionResult.Error("Invalid page reference: the provided token is malformed or corrupted.");
            }

            // Budget authority per Q18: reference budget is authoritative when sole input
            var effectiveBudget = refData.SafeBudgetTokens;

            // Budget mismatch check: only when agent also provides explicit budget
            if (explicitBudgetProvided && resolvedBudget != refData.SafeBudgetTokens)
            {
                return PageResolutionResult.Error(
                    $"Budget mismatch: page reference was created with budget {refData.SafeBudgetTokens} tokens, " +
                    $"but current request resolves to {resolvedBudget} tokens. " +
                    "Re-request with matching budget or start fresh without a page reference.");
            }

            return PageResolutionResult.Success(
                refData.PageNumber,
                refData.DataFingerprint,
                refData.RequestParams,
                effectiveBudget);
        }

        if (pageNumber != null)
        {
            return PageResolutionResult.Success(
                pageNumber.Value,
                fingerprint: null,
                decodedParams: null,
                resolvedBudget);
        }

        // Default: page 1
        return PageResolutionResult.Success(1, null, null, resolvedBudget);
    }

    // --- Parameter Validation (FR-016/Q19) ---

    /// <summary>
    /// MUST validate that decoded params match provided params when both are present.
    /// Returns error message or null if valid.
    /// </summary>
    public static string? ValidateParameterMatch(
        JsonElement? decodedParams,
        string paramName,
        JsonElement? providedValue)
    {
        if (decodedParams == null || providedValue == null)
            return null;

        if (!decodedParams.Value.TryGetProperty(paramName, out var decodedValue))
            return null;

        // Compare values
        if (!JsonElementEquals(decodedValue, providedValue.Value))
        {
            return $"Parameter mismatch: '{paramName}' in page reference is " +
                   $"{FormatJsonValue(decodedValue)} but provided value is {FormatJsonValue(providedValue.Value)}.";
        }

        return null;
    }

    // --- Pagination Metadata ---

    /// <summary>
    /// Builds PaginationMetadataResult with self-reference and optional next-reference.
    /// </summary>
    public static PaginationMetadataResult BuildPaginationMetadata(
        PageAllocation allocation,
        int pageNumber,
        IPageReferenceCodec codec,
        string toolName,
        JsonElement requestParams,
        int safeBudgetTokens,
        string? dataFingerprint)
    {
        var currentRefData = new PageReferenceData(
            toolName, requestParams, safeBudgetTokens, pageNumber, dataFingerprint);

        var hasMore = pageNumber < allocation.TotalPages;

        string? nextRef = null;
        if (hasMore)
        {
            var nextRefData = new PageReferenceData(
                toolName, requestParams, safeBudgetTokens, pageNumber + 1, dataFingerprint);
            nextRef = codec.Encode(nextRefData);
        }

        return new PaginationMetadataResult
        {
            CurrentPage = pageNumber,
            TotalPages = allocation.TotalPages,
            HasMore = hasMore,
            CurrentPageReference = codec.Encode(currentRefData),
            NextPageReference = nextRef
        };
    }

    // --- Extended Manifest Summary ---

    /// <summary>
    /// Builds manifest summary with Feature 004 pagination extension fields.
    /// </summary>
    public static ManifestSummaryResult BuildExtendedManifestSummary(
        PageSlice pageSlice,
        PageAllocation allocation,
        int safeBudgetTokens)
    {
        var includedCount = pageSlice.Items.Count(i => i.Status == PackingItemStatus.Included);
        var partialCount = pageSlice.Items.Count(i => i.Status == PackingItemStatus.Partial);
        var budgetUsed = pageSlice.BudgetUsed;
        var totalOverhead = PaginationConstants.PaginationOverhead +
                            PaginationConstants.BaseManifestOverhead +
                            pageSlice.Items.Count * PaginationConstants.PerItemManifestOverhead;
        var budgetRemaining = Math.Max(0, safeBudgetTokens - totalOverhead - budgetUsed);
        var utilization = safeBudgetTokens > 0
            ? Math.Round((double)budgetUsed / safeBudgetTokens * 100.0, 1)
            : 0.0;

        // Remaining items after this page
        var itemsOnThisPage = pageSlice.Items.Count;
        var itemsAfterThisPage = 0;
        for (var i = pageSlice.PageNumber; i < allocation.TotalPages; i++)
        {
            itemsAfterThisPage += allocation.Pages[i].Items.Count;
        }

        return new ManifestSummaryResult
        {
            TotalItems = allocation.TotalItems,
            IncludedCount = includedCount,
            PartialCount = partialCount,
            DeferredCount = 0, // In paginated mode, every item appears on some page
            TotalBudgetTokens = safeBudgetTokens,
            BudgetUsed = budgetUsed,
            BudgetRemaining = budgetRemaining,
            UtilizationPercent = utilization,
            // Feature 004 extension fields
            IncludedOnThisPage = itemsOnThisPage,
            RemainingAfterThisPage = itemsAfterThisPage,
            TotalPages = allocation.TotalPages
        };
    }

    // --- Staleness Detection ---

    /// <summary>
    /// Checks for data staleness. Returns null when no warning needed.
    /// Per Q20: does NOT apply to pageNumber mode (no embedded fingerprint).
    /// Per spec: does NOT apply to local tools.
    /// </summary>
    public static StalenessWarningResult? CheckStaleness(
        string? originalFingerprint,
        string? currentFingerprint,
        bool isPageReferenceMode)
    {
        // No staleness check for pageNumber mode (Q20) or when no fingerprints
        if (!isPageReferenceMode || originalFingerprint == null || currentFingerprint == null)
            return null;

        if (string.Equals(originalFingerprint, currentFingerprint, StringComparison.Ordinal))
            return null;

        return new StalenessWarningResult
        {
            Message = "PR data has changed since pagination started. Page boundaries may differ from earlier pages. Consider restarting from page 1.",
            OriginalFingerprint = originalFingerprint,
            CurrentFingerprint = currentFingerprint
        };
    }

    // --- Helpers ---

    private static bool JsonElementEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind)
        {
            // Allow number comparison across kinds (int/long)
            if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number)
            {
                return a.TryGetInt64(out var aVal) && b.TryGetInt64(out var bVal) && aVal == bVal;
            }
            return false;
        }

        return a.ValueKind switch
        {
            JsonValueKind.Number => a.TryGetInt64(out var aVal) && b.TryGetInt64(out var bVal) && aVal == bVal,
            JsonValueKind.String => string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal),
            JsonValueKind.True or JsonValueKind.False => true, // same kind already checked
            JsonValueKind.Null => true,
            _ => a.GetRawText() == b.GetRawText()
        };
    }

    private static string FormatJsonValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.String => $"\"{element.GetString()}\"",
            _ => element.GetRawText()
        };
    }
}

/// <summary>
/// Result of page resolution — either success with page context or an error message.
/// </summary>
internal sealed class PageResolutionResult
{
    public bool IsSuccess { get; private init; }
    public string? ErrorMessage { get; private init; }
    public int PageNumber { get; private init; }
    public string? Fingerprint { get; private init; }
    public JsonElement? DecodedParams { get; private init; }
    public int ResolvedBudget { get; private init; }

    public static PageResolutionResult Success(
        int pageNumber,
        string? fingerprint,
        JsonElement? decodedParams,
        int resolvedBudget)
    {
        return new PageResolutionResult
        {
            IsSuccess = true,
            PageNumber = pageNumber,
            Fingerprint = fingerprint,
            DecodedParams = decodedParams,
            ResolvedBudget = resolvedBudget
        };
    }

    public static PageResolutionResult Error(string message)
    {
        return new PageResolutionResult
        {
            IsSuccess = false,
            ErrorMessage = message
        };
    }
}
