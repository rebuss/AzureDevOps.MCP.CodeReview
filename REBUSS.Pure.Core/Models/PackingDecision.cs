namespace REBUSS.Pure.Core.Models.ResponsePacking;

/// <summary>
/// Result returned by <see cref="IResponsePacker.Pack"/>.
/// Contains per-item decisions and a ready-to-map content manifest.
/// </summary>
/// <param name="Items">One entry per candidate, preserving original order.</param>
/// <param name="Manifest">Content manifest for the output DTO.</param>
public sealed record PackingDecision(
    IReadOnlyList<PackingDecisionItem> Items,
    ContentManifest Manifest);
