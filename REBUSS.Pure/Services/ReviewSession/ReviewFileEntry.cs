using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.Services.ReviewSession;

/// <summary>
/// Per-file state within a <see cref="ReviewSession"/>. Mutated only under the
/// parent session's lock. See data-model.md for invariants.
///
/// Feature 014 added immutable <see cref="Classification"/>, <see cref="MatchedPattern"/>,
/// <see cref="LinesAdded"/>, and <see cref="LinesRemoved"/> fields, captured at
/// session-begin time and never re-evaluated.
/// </summary>
public sealed class ReviewFileEntry
{
    public ReviewFileEntry(
        string path,
        FileCategory category,
        int estimatedTokens,
        ReviewFileClassification classification = ReviewFileClassification.Deep,
        string? matchedPattern = null,
        int linesAdded = 0,
        int linesRemoved = 0)
    {
        Path = path;
        Category = category;
        EstimatedTokens = estimatedTokens;
        Classification = classification;
        MatchedPattern = matchedPattern;
        LinesAdded = linesAdded;
        LinesRemoved = linesRemoved;
        Status = ReviewItemStatus.Pending;
        Observations = new List<ObservationRecord>();
    }

    public string Path { get; }
    public FileCategory Category { get; }
    public int EstimatedTokens { get; }

    /// <summary>Feature 014: deep (full content) or scan (synthetic summary). Captured at session-begin; immutable.</summary>
    public ReviewFileClassification Classification { get; }

    /// <summary>Feature 014: the first pattern in configuration order that matched this file's path, or null when <see cref="Classification"/> is <see cref="ReviewFileClassification.Deep"/>.</summary>
    public string? MatchedPattern { get; }

    /// <summary>Feature 014: lines added — used by the synthetic summary.</summary>
    public int LinesAdded { get; }

    /// <summary>Feature 014: lines removed — used by the synthetic summary.</summary>
    public int LinesRemoved { get; }

    public ReviewItemStatus Status { get; internal set; }

    /// <summary>1-based index of the most recently delivered chunk; null until any delivery.</summary>
    public int? CurrentChunkIndex { get; internal set; }

    /// <summary>Total chunk count for this file; null for files that fit in one response.</summary>
    public int? TotalChunks { get; internal set; }

    /// <summary>Lazily computed by <see cref="ReviewSession"/> on first oversize delivery; cached for the session lifetime.</summary>
    public IReadOnlyList<string>? Chunks { get; internal set; }

    public List<ObservationRecord> Observations { get; }

    public DateTimeOffset? DeliveredAt { get; internal set; }
    public DateTimeOffset? LastAcknowledgedAt { get; internal set; }
}
