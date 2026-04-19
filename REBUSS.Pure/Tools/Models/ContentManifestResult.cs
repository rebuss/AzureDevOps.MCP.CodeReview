using System.Text.Json.Serialization;
using REBUSS.Pure.Core.Models.ResponsePacking;

namespace REBUSS.Pure.Tools.Models;

/// <summary>
/// JSON output DTO for the content manifest included in tool responses.
/// Wraps <see cref="ManifestEntryResult"/> items and a <see cref="ManifestSummaryResult"/>.
/// </summary>
public sealed class ContentManifestResult
{
    [JsonPropertyName("items")]
    public List<ManifestEntryResult> Items { get; set; } = new();

    [JsonPropertyName("summary")]
    public ManifestSummaryResult Summary { get; set; } = new();

    /// <summary>
    /// Creates a <see cref="ContentManifestResult"/> from a domain <see cref="ContentManifest"/>.
    /// </summary>
    internal static ContentManifestResult From(ContentManifest manifest) =>
        new()
        {
            Items = manifest.Items.Select(e => new ManifestEntryResult
            {
                Path = e.Path,
                EstimatedTokens = e.EstimatedTokens,
                Status = e.Status.ToString(),
                PriorityTier = e.PriorityTier
            }).ToList(),
            Summary = new ManifestSummaryResult
            {
                TotalItems = manifest.Summary.TotalItems,
                IncludedCount = manifest.Summary.IncludedCount,
                PartialCount = manifest.Summary.PartialCount,
                DeferredCount = manifest.Summary.DeferredCount,
                TotalBudgetTokens = manifest.Summary.TotalBudgetTokens,
                BudgetUsed = manifest.Summary.BudgetUsed,
                BudgetRemaining = manifest.Summary.BudgetRemaining,
                UtilizationPercent = manifest.Summary.UtilizationPercent,
                IncludedOnThisPage = manifest.Summary.IncludedOnThisPage,
                RemainingAfterThisPage = manifest.Summary.RemainingAfterThisPage,
                TotalPages = manifest.Summary.TotalPages
            }
        };
}
