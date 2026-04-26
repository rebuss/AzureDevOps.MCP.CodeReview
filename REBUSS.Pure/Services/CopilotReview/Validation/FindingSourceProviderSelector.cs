using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core;
using REBUSS.Pure.Services.LocalReview;

namespace REBUSS.Pure.Services.CopilotReview.Validation;

/// <summary>
/// Picks the correct <see cref="IFindingSourceProvider"/> per review key. Local prefixes
/// receive a per-call <see cref="BoundLocalSourceProvider"/> wrapper that captures the
/// matching git ref and de-duplicates the workspace-root-missing warning to one log
/// entry per review (FR-006). Non-local keys receive the singleton remote provider.
/// </summary>
public sealed class FindingSourceProviderSelector : IFindingSourceProviderSelector
{
    private readonly RemoteArchiveSourceProvider _remote;
    private readonly LocalWorkspaceSourceProvider _local;
    private readonly ILogger<FindingSourceProviderSelector> _logger;

    public FindingSourceProviderSelector(
        RemoteArchiveSourceProvider remote,
        LocalWorkspaceSourceProvider local,
        ILogger<FindingSourceProviderSelector> logger)
    {
        _remote = remote;
        _local = local;
        _logger = logger;
    }

    public IFindingSourceProvider SelectFor(string reviewKey)
    {
        // Prefixes mirror `LocalReviewScope.ToString()` exactly:
        //   Staged       → "staged"
        //   WorkingTree  → "working-tree"
        //   BranchDiff   → "branch-diff:<base>"
        // The handler builds the key as $"local:{scope}:{repoRoot}", so any divergence here
        // silently routes local reviews to the remote (PR-archive) provider — the validator
        // then degrades every finding to "uncertain" with no obvious diagnostic.
        if (reviewKey.StartsWith("local:staged:", StringComparison.Ordinal))
            return new BoundLocalSourceProvider(_local, LocalGitClient.IndexRef, _logger);
        if (reviewKey.StartsWith("local:working-tree:", StringComparison.Ordinal))
            return new BoundLocalSourceProvider(_local, LocalGitClient.WorkingTreeRef, _logger);
        if (reviewKey.StartsWith("local:branch-diff:", StringComparison.Ordinal))
            return new BoundLocalSourceProvider(_local, "HEAD", _logger);
        return _remote;
    }

    /// <summary>
    /// Per-call wrapper around <see cref="LocalWorkspaceSourceProvider"/>. Carries the
    /// bound git ref and the per-review warning de-duplication state. Not registered in
    /// DI — instantiated by the selector for one review pass; mutating
    /// <see cref="_warnedRootMissing"/> is local to this allocation only.
    /// </summary>
    private sealed class BoundLocalSourceProvider : IFindingSourceProvider
    {
        private readonly LocalWorkspaceSourceProvider _local;
        private readonly string _gitRef;
        private readonly ILogger _logger;
        private bool _warnedRootMissing;

        public BoundLocalSourceProvider(
            LocalWorkspaceSourceProvider local,
            string gitRef,
            ILogger logger)
        {
            _local = local;
            _gitRef = gitRef;
            _logger = logger;
        }

        public Task<string?> GetAfterCodeAsync(string filePath, CancellationToken cancellationToken) =>
            _local.GetAfterCodeAsync(filePath, _gitRef, OnRootMissing, cancellationToken);

        private void OnRootMissing()
        {
            if (_warnedRootMissing) return;
            _warnedRootMissing = true;
            _logger.LogWarning(
                "Local workspace root unresolvable; finding validation will degrade to 'uncertain' for this review.");
        }
    }
}
