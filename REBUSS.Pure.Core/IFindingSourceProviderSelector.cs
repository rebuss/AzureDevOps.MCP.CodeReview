namespace REBUSS.Pure.Core;

/// <summary>
/// Selects an <see cref="IFindingSourceProvider"/> appropriate for a given review.
/// Local prefixes (<c>local:staged:</c>, <c>local:working-tree:</c>, <c>local:branch-diff:</c>)
/// — matching <c>LocalReviewScope.ToString()</c> — map to the local-workspace provider bound
/// to the matching git ref. All other review keys map to the PR-archive provider.
/// </summary>
public interface IFindingSourceProviderSelector
{
    /// <summary>
    /// Returns the provider appropriate for <paramref name="reviewKey"/>. The returned
    /// instance may be a per-call wrapper carrying mode-specific state (e.g., the bound
    /// git ref, or per-review warning de-duplication state) — callers MUST treat it as
    /// scoped to the current review and not cache it across reviews.
    /// </summary>
    IFindingSourceProvider SelectFor(string reviewKey);
}
