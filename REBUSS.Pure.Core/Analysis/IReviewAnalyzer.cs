namespace REBUSS.Pure.Core.Analysis;

/// <summary>
/// A pluggable analysis feature that examines a change set and produces
/// a specific section of the review context.
///
/// Analyzers are registered in DI as <c>IEnumerable&lt;IReviewAnalyzer&gt;</c>
/// and orchestrated by <see cref="ReviewContextOrchestrator"/>.
/// </summary>
public interface IReviewAnalyzer
{
    /// <summary>
    /// Unique key identifying this analyzer's output section (e.g. "impact_graph", "contracts", "test_mapping").
    /// Used as the key in the final <see cref="ReviewContext"/> dictionary.
    /// </summary>
    string SectionKey { get; }

    /// <summary>
    /// Human-readable name for logging and diagnostics.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Priority for execution ordering. Lower values run first.
    /// Analyzers that produce data consumed by others should have lower priority.
    /// </summary>
    int Order { get; }

    /// <summary>
    /// Returns <c>true</c> if this analyzer can operate on the given input.
    /// For example, an impact graph analyzer might require a local checkout.
    /// </summary>
    bool CanAnalyze(AnalysisInput input);

    /// <summary>
    /// Performs analysis and returns a context section.
    /// Returns <c>null</c> if the analyzer produces no output for this input.
    /// </summary>
    Task<AnalysisSection?> AnalyzeAsync(AnalysisInput input, CancellationToken ct = default);
}
