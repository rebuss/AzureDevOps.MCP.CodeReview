using System.Text.Json.Serialization;

namespace REBUSS.Pure.Tools.Models;

/// <summary>
/// Output DTO carrying context budget metadata in tool responses.
/// Created by Feature 002 (Context Window Awareness) for contract stability.
/// Embedded in tool responses by Feature 003 (Context-Aware Response Packing).
/// </summary>
public class ContextBudgetMetadata
{
    [JsonPropertyName("totalBudgetTokens")]
    public int TotalBudgetTokens { get; set; }

    [JsonPropertyName("safeBudgetTokens")]
    public int SafeBudgetTokens { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("estimatedTokensUsed")]
    public int? EstimatedTokensUsed { get; set; }

    [JsonPropertyName("percentageUsed")]
    public double? PercentageUsed { get; set; }

    [JsonPropertyName("warnings")]
    public string[]? Warnings { get; set; }
}
