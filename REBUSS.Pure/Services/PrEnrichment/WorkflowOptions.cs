namespace REBUSS.Pure.Services.PrEnrichment;

/// <summary>
/// Workflow timeouts for the progressive PR metadata feature.
/// Bound from the <c>Workflow</c> section of <c>appsettings.json</c>.
/// </summary>
public sealed class WorkflowOptions
{
    public const string SectionName = "Workflow";

    /// <summary>
    /// Internal timeout (milliseconds) the metadata tool enforces before
    /// falling back to the basic-summary response. Must be strictly less than
    /// the host's hard tool-call ceiling (typically ~30 000 ms) so the response
    /// has time to serialize and return.
    /// Default: 28 000 ms.
    /// </summary>
    public int MetadataInternalTimeoutMs { get; set; } = 28_000;

    /// <summary>
    /// Internal timeout (milliseconds) the content tool enforces before
    /// returning a friendly "still preparing" status. Same constraint as
    /// <see cref="MetadataInternalTimeoutMs"/>.
    /// Default: 28 000 ms.
    /// </summary>
    public int ContentInternalTimeoutMs { get; set; } = 28_000;

    /// <summary>
    /// Nested options for the stateful PR review session (feature 012 onwards).
    /// </summary>
    public ReviewSessionOptions ReviewSession { get; set; } = new();
}

/// <summary>
/// Options consumed by the review-session subsystem. Bound from the
/// <c>Workflow:ReviewSession</c> section of <c>appsettings.json</c>.
/// </summary>
public sealed class ReviewSessionOptions
{
    /// <summary>
    /// Glob patterns identifying mechanical / auto-generated files that should be
    /// delivered as a synthetic summary instead of full content during a review
    /// session (feature 014). Empty list means every file is reviewed in full.
    /// </summary>
    public string[] ScanOnlyPatterns { get; set; } = Array.Empty<string>();
}
