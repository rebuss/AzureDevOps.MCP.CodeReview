namespace REBUSS.Pure.Core.Models.ResponsePacking;

/// <summary>
/// One item in the <see cref="ContentManifest"/> describing a candidate's inclusion status.
/// </summary>
/// <param name="Path">File path matching the original candidate.</param>
/// <param name="EstimatedTokens">Token estimate for the item.</param>
/// <param name="Status">Inclusion status: Included, Partial, or Deferred.</param>
/// <param name="PriorityTier">File category name (e.g. "Source", "Test").</param>
public sealed record ManifestEntry(
    string Path,
    int EstimatedTokens,
    PackingItemStatus Status,
    string PriorityTier);
