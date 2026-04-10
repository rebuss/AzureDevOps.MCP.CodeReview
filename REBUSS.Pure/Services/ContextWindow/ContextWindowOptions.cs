namespace REBUSS.Pure.Services.ContextWindow;

/// <summary>
/// Configuration options for the Context Window Awareness feature.
/// Loaded from the "ContextWindow" section of appsettings.json via IOptions&lt;T&gt;.
/// </summary>
public sealed class ContextWindowOptions
{
    public const string SectionName = "ContextWindow";

    /// <summary>
    /// Percentage of total context window reserved for agent reasoning and output.
    /// Valid range: 1–90. Values outside this range are clamped.
    /// </summary>
    public int SafetyMarginPercent { get; set; } = 30;

    /// <summary>
    /// Characters-per-token ratio used for token estimation.
    /// Must be greater than 0. Invalid values fall back to 4.0.
    /// </summary>
    public double CharsPerToken { get; set; } = 4.0;

    /// <summary>
    /// Safe default budget (in tokens) when no context window is declared.
    /// </summary>
    public int DefaultBudgetTokens { get; set; } = 128_000;

    /// <summary>
    /// Minimum enforceable budget in tokens.
    /// </summary>
    public int MinBudgetTokens { get; set; } = 4_000;

    /// <summary>
    /// Maximum enforceable budget cap in tokens.
    /// </summary>
    public int MaxBudgetTokens { get; set; } = 2_000_000;

    /// <summary>
    /// Hard cap imposed by the API gateway or MCP host (e.g. GitHub Copilot proxy).
    /// When set to a positive value, the resolved budget is clamped to this limit
    /// <em>before</em> the safety margin is applied — regardless of model registry
    /// or explicit token declarations.
    /// Set to <c>null</c> or <c>0</c> to disable (e.g. for direct API access).
    /// </summary>
    public int? GatewayMaxTokens { get; set; }

    /// <summary>
    /// Model identifier to total context window size (tokens) mapping.
    /// Keys are compared case-insensitively.
    /// </summary>
    public Dictionary<string, int> ModelRegistry { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
