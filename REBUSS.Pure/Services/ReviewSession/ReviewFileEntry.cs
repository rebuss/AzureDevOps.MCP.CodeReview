using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.Services.ReviewSession;

/// <summary>
/// Per-file state within a <see cref="ReviewSession"/>. Mutated only under the
/// parent session's lock. See data-model.md for invariants.
/// </summary>
public sealed class ReviewFileEntry
{
    public ReviewFileEntry(string path, FileCategory category, int estimatedTokens)
    {
        Path = path;
        Category = category;
        EstimatedTokens = estimatedTokens;
        Status = ReviewItemStatus.Pending;
        Observations = new List<ObservationRecord>();
    }

    public string Path { get; }
    public FileCategory Category { get; }
    public int EstimatedTokens { get; }

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
