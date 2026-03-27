namespace REBUSS.Pure.Core.Models.ResponsePacking;

/// <summary>
/// Domain-level content manifest describing the packing result.
/// Mapped to <c>ContentManifestResult</c> for JSON output.
/// </summary>
/// <param name="Items">One entry per candidate item.</param>
/// <param name="Summary">Aggregated counts and budget statistics.</param>
public sealed record ContentManifest(
    IReadOnlyList<ManifestEntry> Items,
    ManifestSummary Summary);
