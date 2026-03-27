namespace REBUSS.Pure.Core.Models.ResponsePacking;

/// <summary>
/// Inclusion decision for a candidate item in the packing result.
/// </summary>
public enum PackingItemStatus
{
    /// <summary>Item fits within budget and is included in full.</summary>
    Included,

    /// <summary>Item is the highest-priority oversized item; only some hunks are included.</summary>
    Partial,

    /// <summary>Item does not fit and is excluded from this response.</summary>
    Deferred
}
