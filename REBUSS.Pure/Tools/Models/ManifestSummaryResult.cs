using System.Text.Json.Serialization;

namespace REBUSS.Pure.Tools.Models;

/// <summary>
/// JSON output DTO for the manifest summary with aggregated packing statistics.
/// </summary>
public sealed class ManifestSummaryResult
{
    [JsonPropertyName("totalItems")]
    public int TotalItems { get; set; }

    [JsonPropertyName("includedCount")]
    public int IncludedCount { get; set; }

    [JsonPropertyName("partialCount")]
    public int PartialCount { get; set; }

    [JsonPropertyName("deferredCount")]
    public int DeferredCount { get; set; }

    [JsonPropertyName("totalBudgetTokens")]
    public int TotalBudgetTokens { get; set; }

    [JsonPropertyName("budgetUsed")]
    public int BudgetUsed { get; set; }

    [JsonPropertyName("budgetRemaining")]
    public int BudgetRemaining { get; set; }

    [JsonPropertyName("utilizationPercent")]
    public double UtilizationPercent { get; set; }

    /// <summary>Items included on the current page (null in non-paginated mode → omitted). Feature 004.</summary>
    [JsonPropertyName("includedOnThisPage")]
    public int? IncludedOnThisPage { get; set; }

    /// <summary>Items on subsequent pages (null in non-paginated mode → omitted). Feature 004.</summary>
    [JsonPropertyName("remainingAfterThisPage")]
    public int? RemainingAfterThisPage { get; set; }

    /// <summary>Total number of pages (null in non-paginated mode → omitted). Feature 004.</summary>
    [JsonPropertyName("totalPages")]
    public int? TotalPages { get; set; }
}
