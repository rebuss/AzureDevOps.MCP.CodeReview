namespace REBUSS.Pure.Core.Analysis;

/// <summary>
/// Aggregated output from all <see cref="IReviewAnalyzer"/>s for a single pull request review.
/// </summary>
public sealed class ReviewContext
{
    public int PrNumber { get; init; }
    public Dictionary<string, AnalysisSection> Sections { get; init; } = new();
}
