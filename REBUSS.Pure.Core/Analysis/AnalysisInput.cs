using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.Core.Analysis;

/// <summary>
/// All the data available to analyzers for a single review.
/// Built by the orchestrator from the provider's output.
/// </summary>
public sealed record AnalysisInput
{
    /// <summary>The structured diff (changed files with hunks).</summary>
    public required PullRequestDiff Diff { get; init; }

    /// <summary>Rich PR metadata (title, description, author, branches, etc.).</summary>
    public required FullPullRequestMetadata Metadata { get; init; }

    /// <summary>Classified file list with review priority.</summary>
    public required PullRequestFiles Files { get; init; }

    /// <summary>
    /// Optional local repository root path, available when the repo has been checked out.
    /// Analyzers that need filesystem access (e.g. semantic parsing) should check this.
    /// </summary>
    public string? LocalRepositoryRoot { get; init; }

    /// <summary>
    /// Sections already produced by earlier analyzers (for chaining).
    /// Keyed by <see cref="IReviewAnalyzer.SectionKey"/>.
    /// </summary>
    public Dictionary<string, AnalysisSection> PreviousSections { get; init; } = new();
}
