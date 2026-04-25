using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core;
using REBUSS.Pure.Services.LocalReview;

namespace REBUSS.Pure.Services.CopilotReview.Validation;

/// <summary>
/// Local-mode finding source provider. Reads the file's after-state directly from the
/// developer's working repository via <see cref="ILocalGitClient.GetFileContentAtRefAsync"/>.
/// The git ref is supplied per call by the selector's per-review wrapper so the validator
/// always reads the exact bytes the diff under review presented:
/// <list type="bullet">
///   <item><c>local:staged</c> → <see cref="LocalGitClient.IndexRef"/> (stage-0 index)</item>
///   <item><c>local:unstaged</c> → <see cref="LocalGitClient.WorkingTreeRef"/> (on-disk)</item>
///   <item><c>local:branch:&lt;branch&gt;</c> → <c>"HEAD"</c> (current branch HEAD)</item>
/// </list>
/// Workspace root is resolved per call (Constitution §V) so that a workspace re-rooted
/// mid-session is honored on the next review.
/// </summary>
public sealed class LocalWorkspaceSourceProvider
{
    private const int MaxFileSizeBytes = 100 * 1024;

    private readonly IWorkspaceRootProvider _workspaceRootProvider;
    private readonly ILocalGitClient _localGitClient;
    private readonly ILogger<LocalWorkspaceSourceProvider> _logger;

    public LocalWorkspaceSourceProvider(
        IWorkspaceRootProvider workspaceRootProvider,
        ILocalGitClient localGitClient,
        ILogger<LocalWorkspaceSourceProvider> logger)
    {
        _workspaceRootProvider = workspaceRootProvider;
        _localGitClient = localGitClient;
        _logger = logger;
    }

    /// <summary>
    /// Returns the after-state of <paramref name="filePath"/> at <paramref name="gitRef"/>,
    /// or <c>null</c> if unavailable. Invokes <paramref name="onWorkspaceRootMissing"/>
    /// exactly when the workspace root cannot be resolved — the caller (per-review wrapper)
    /// is responsible for de-duplicating the resulting warning across the review (FR-006).
    /// </summary>
    public async Task<string?> GetAfterCodeAsync(
        string filePath,
        string gitRef,
        Action onWorkspaceRootMissing,
        CancellationToken cancellationToken)
    {
        var root = _workspaceRootProvider.ResolveRepositoryRoot();
        if (string.IsNullOrEmpty(root))
        {
            onWorkspaceRootMissing();
            return null;
        }

        string? content;
        try
        {
            content = await _localGitClient
                .GetFileContentAtRefAsync(root, filePath, gitRef, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Local git read failed for {FilePath} at ref {GitRef}", filePath, gitRef);
            return null;
        }

        if (content is null)
        {
            _logger.LogDebug("File {FilePath} not present at ref {GitRef} in local workspace", filePath, gitRef);
            return null;
        }

        // Cheap upper-bound check: char count >= byte count for any encoding we read.
        if (content.Length > MaxFileSizeBytes)
        {
            _logger.LogDebug("File {FilePath} exceeds size limit ({Length} chars), validation source unavailable", filePath, content.Length);
            return null;
        }

        return content;
    }
}
