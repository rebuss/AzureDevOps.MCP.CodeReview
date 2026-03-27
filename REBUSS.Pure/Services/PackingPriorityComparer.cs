using REBUSS.Pure.Core.Models.ResponsePacking;

namespace REBUSS.Pure.Services.ResponsePacking;

/// <summary>
/// Compares <see cref="PackingCandidate"/> items for priority ordering.
/// Sort order: FileCategory ascending (Source first) → TotalChanges descending → Path ascending.
/// </summary>
internal sealed class PackingPriorityComparer : IComparer<PackingCandidate>
{
    public static readonly PackingPriorityComparer Instance = new();

    public int Compare(PackingCandidate? x, PackingCandidate? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        // Primary: FileCategory ascending (Source = 0 is highest priority)
        var categoryCompare = x.Category.CompareTo(y.Category);
        if (categoryCompare != 0) return categoryCompare;

        // Secondary: TotalChanges descending (more changes = higher priority)
        var changesCompare = y.TotalChanges.CompareTo(x.TotalChanges);
        if (changesCompare != 0) return changesCompare;

        // Tertiary: Path ascending (deterministic tiebreaker)
        return string.Compare(x.Path, y.Path, StringComparison.Ordinal);
    }
}
