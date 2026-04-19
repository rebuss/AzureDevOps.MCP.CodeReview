namespace REBUSS.Pure.RoslynProcessor;

/// <summary>
/// Classification of a detected structural change between before and after syntax trees.
/// </summary>
public enum StructuralChangeKind
{
    /// <summary>Method/property/constructor signature differs (params, return type, modifiers).</summary>
    SignatureChanged,

    /// <summary>Member exists in after but not in before.</summary>
    MemberAdded,

    /// <summary>Member exists in before but not in after.</summary>
    MemberRemoved,

    /// <summary>Type (class/struct/record/interface/enum) added.</summary>
    TypeAdded,

    /// <summary>Type removed.</summary>
    TypeRemoved,

    /// <summary>Base type or implemented interfaces changed.</summary>
    BaseTypeChanged
}

/// <summary>
/// A single detected structural difference between before and after C# source.
/// </summary>
/// <param name="Kind">The classification of the change.</param>
/// <param name="Description">Human-readable description (e.g., "ProcessOrder(Order) → ProcessOrder(Order, CancellationToken)").</param>
/// <param name="LineNumber">1-based line in the after file for positioning; null for removed items.</param>
public sealed record StructuralChange
{
    public required StructuralChangeKind Kind { get; init; }
    public required string Description { get; init; }
    public int? LineNumber { get; init; }
}
