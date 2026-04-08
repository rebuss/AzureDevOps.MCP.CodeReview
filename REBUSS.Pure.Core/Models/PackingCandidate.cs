namespace REBUSS.Pure.Core.Models.ResponsePacking;

/// <summary>
/// A candidate item submitted to the packing service for budget consideration.
/// </summary>
/// <param name="Path">File path — unique identifier within the request.</param>
/// <param name="EstimatedTokens">Pre-computed token estimate from <see cref="ITokenEstimator"/>.</param>
/// <param name="Category">File category from <see cref="FileCategory"/> — determines primary sort order.</param>
/// <param name="TotalChanges">Total additions + deletions — determines secondary sort within the same category. By construction equals <paramref name="LinesAdded"/> + <paramref name="LinesRemoved"/>.</param>
/// <param name="LinesAdded">Lines added in this file (additions). Used by feature 014 scan-only synthetic summaries.</param>
/// <param name="LinesRemoved">Lines removed in this file (deletions). Used by feature 014 scan-only synthetic summaries.</param>
public sealed record PackingCandidate(
    string Path,
    int EstimatedTokens,
    FileCategory Category,
    int TotalChanges,
    int LinesAdded = 0,
    int LinesRemoved = 0);
