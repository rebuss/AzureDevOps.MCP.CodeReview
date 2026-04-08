namespace REBUSS.Pure.Services.ReviewSession;

/// <summary>
/// Splits a single file's enriched plain-text into ordered self-contained chunks
/// that fit under a token budget. Prefers <c>@@ ... @@</c> hunk-header boundaries;
/// pathological hunks larger than the budget are split mid-hunk with an explicit
/// warning marker (FR-023, FR-024). Round-trip invariant: concatenating the
/// returned chunks (modulo warning markers) reproduces the input.
/// </summary>
public interface ISingleFileChunker
{
    IReadOnlyList<string> Split(string enrichedText, int budgetTokens);
}
