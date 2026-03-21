namespace REBUSS.Pure.Core.Analysis;

/// <summary>
/// One section of the review context, produced by a single <see cref="IReviewAnalyzer"/>.
/// </summary>
public sealed class AnalysisSection
{
    /// <summary>Section key (matches the producing analyzer's SectionKey).</summary>
    public required string Key { get; init; }

    /// <summary>Human-readable section title for output.</summary>
    public required string Title { get; init; }

    /// <summary>
    /// Structured content of this section. The shape depends on the analyzer.
    /// Serialized to JSON or Markdown by the output formatter.
    /// </summary>
    public required object Content { get; init; }
}
