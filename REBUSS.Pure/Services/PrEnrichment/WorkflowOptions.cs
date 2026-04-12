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
    /// Polling interval (milliseconds) for checking Copilot review progress
    /// and sending incremental progress notifications to the client. Shorter
    /// intervals give more responsive UX at the cost of snapshot reads.
    /// Default: 2 000 ms.
    /// </summary>
    public int CopilotReviewProgressPollingIntervalMs { get; set; } = 2_000;
}
