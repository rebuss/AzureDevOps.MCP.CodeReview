using REBUSS.Pure.Core.Models.ResponsePacking;

namespace REBUSS.Pure.Core;

/// <summary>
/// Selects which candidate items fit within the token budget using a
/// priority-based greedy packing algorithm. Pure function — no state
/// retained between calls.
/// </summary>
public interface IResponsePacker
{
    /// <summary>
    /// Packs the given candidates into a response that fits within the safe budget.
    /// </summary>
    /// <param name="candidates">Candidate items with pre-computed token estimates.</param>
    /// <param name="safeBudgetTokens">Available safe budget in tokens (from Feature 002).</param>
    /// <returns>Packing decision with per-item status and content manifest.</returns>
    PackingDecision Pack(IReadOnlyList<PackingCandidate> candidates, int safeBudgetTokens);
}
