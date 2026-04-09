namespace REBUSS.Pure.Services.CopilotReview;

/// <summary>
/// Operator-facing configuration for the Copilot review layer. Bound via
/// <c>services.Configure&lt;CopilotReviewOptions&gt;(configuration.GetSection(SectionName))</c>
/// and consumed through <c>IOptions&lt;CopilotReviewOptions&gt;.Value</c> at first use
/// (never at DI construction time — Principle V).
/// </summary>
public sealed class CopilotReviewOptions
{
    public const string SectionName = "CopilotReview";

    /// <summary>
    /// Master switch for the copilot-assisted review flow. When <c>false</c>, the
    /// availability detector short-circuits to <c>false</c> without attempting to
    /// start the SDK client, and every <c>get_pr_content</c> call uses the existing
    /// content-only path (FR-003, FR-013).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Per-call Copilot context budget in tokens. The orchestrator uses this to
    /// re-allocate the enrichment result into Copilot-sized pages (research.md
    /// Decision 7). Default matches the IDE gateway's assumption.
    /// </summary>
    public int ReviewBudgetTokens { get; set; } = 128_000;

    /// <summary>
    /// Copilot model identifier passed to <c>SessionConfig.Model</c>. Default is
    /// <c>"claude-sonnet-4.6"</c>. If the installed SDK rejects this string, verify
    /// the canonical form via <c>client.ListModelsAsync()</c> and update this value.
    /// </summary>
    public string Model { get; set; } = "claude-sonnet-4.6";
}
