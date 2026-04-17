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
    /// availability detector short-circuits to <c>false</c> and all review tool
    /// calls return an error instructing the user to enable Copilot SDK integration.
    /// There is no content-only fallback.
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

    /// <summary>
    /// Optional pre-minted, Copilot-entitled GitHub token override. Lower priority
    /// than the <see cref="GitHubTokenEnvironmentVariable"/> environment variable;
    /// higher priority than the default gh-CLI logged-in-user session. Classic
    /// GitHub Personal Access Tokens are <b>NOT</b> valid Copilot-entitled tokens
    /// and will fail verification (FR-013). Blank / whitespace values are treated
    /// as unset (FR-012). Feature 018 (FR-009).
    /// </summary>
    public string? GitHubToken { get; set; }

    /// <summary>
    /// When <c>true</c>, a verification failure throws
    /// <c>CopilotUnavailableException</c> from
    /// <c>ICopilotAvailabilityDetector.IsAvailableAsync</c> instead of returning
    /// <c>false</c>. Strict mode is <b>lazy</b> — it only fires from the first
    /// review request, never from server startup (FR-015). Default <c>false</c>
    /// (graceful degradation, FR-014). FR-016 takes precedence: when
    /// <see cref="Enabled"/> is <c>false</c>, strict mode does NOT escalate the
    /// disabled-by-config short-circuit into a throw.
    /// </summary>
    public bool StrictMode { get; set; } = false;

    /// <summary>
    /// Environment variable name for the token override channel (FR-010).
    /// Highest priority in the token resolution chain (FR-011). Note: the value
    /// at this env var must be a Copilot-entitled OAuth token; classic PATs
    /// will fail verification.
    /// </summary>
    public const string GitHubTokenEnvironmentVariable = "REBUSS_COPILOT_TOKEN";

    // ─── Feature 021: Finding validation ────────────────────────────────────

    /// <summary>
    /// When <c>true</c>, each page's findings are re-checked against the full source
    /// of their enclosing method/scope via a second Copilot call. False positives are
    /// filtered from the final output. Feature 021 (FR-014).
    /// </summary>
    public bool ValidateFindings { get; set; } = true;

    /// <summary>
    /// If the total number of findings across all pages exceeds this threshold, validation
    /// is skipped entirely (likely a systemic issue, not individual false positives). Feature 021 (FR-015).
    /// </summary>
    public int MaxValidatableFindings { get; set; } = 40;

    /// <summary>
    /// Truncate method bodies exceeding this line count, centered on the finding's line.
    /// Bounds token usage for validation calls. Feature 021 (FR-016).
    /// </summary>
    public int MaxScopeLines { get; set; } = 150;

    /// <summary>
    /// Maximum number of pages reviewed concurrently. The orchestrator dispatches pages in
    /// batches of this size, awaits every response in the batch, then starts the next batch.
    /// Caps the number of simultaneously in-flight Copilot requests — the GitHub Copilot
    /// backend rate-limits a larger fan-out and silently re-queues the overflow, doubling
    /// wall-clock time. Values &lt; 1 are clamped to 1. Default <c>6</c>.
    /// </summary>
    public int MaxConcurrentPages { get; set; } = 6;

    /// <summary>
    /// Minimum spacing, in seconds, between successive outbound Copilot SDK requests
    /// (<c>CreateSessionAsync</c> and <c>SendAsync</c>). Enforced by
    /// <c>CopilotRequestThrottle</c> via a process-wide semaphore + timestamp gate so
    /// that a parallel page batch (see <see cref="MaxConcurrentPages"/>) does not burst
    /// the GitHub Copilot backend's per-client rate limit. Values &lt; 0 are clamped to
    /// <c>0</c> (no throttling — use only in tests). Default <c>3</c>.
    /// </summary>
    public double MinRequestIntervalSeconds { get; set; } = 3.0;

    /// <summary>
    /// How long a completed / failed / cancelled review job is retained in the orchestrator's
    /// in-memory dictionary before it is swept. Without this cap, a long-running MCP server
    /// accumulates one job entry (plus its <c>CopilotReviewResult</c>, page texts, and task
    /// references) per reviewed PR for the lifetime of the process. Sweep is opportunistic —
    /// runs under the trigger lock on each <c>TriggerReview</c> call.
    /// <para>
    /// Values ≤ 0 disable the sweep (unbounded retention — only recommended for short-lived
    /// processes or tests). Default <c>30</c> minutes is longer than any reasonable
    /// review-consumption window while still reclaiming memory on quiet intervals.
    /// </para>
    /// </summary>
    public int JobRetentionMinutes { get; set; } = 30;
}
