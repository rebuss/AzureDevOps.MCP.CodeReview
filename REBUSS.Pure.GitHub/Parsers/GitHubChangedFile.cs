using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.GitHub.Parsers;

/// <summary>
/// A changed file as returned by GitHub's <c>/pulls/{n}/files</c> endpoint, paired with the
/// optional unified-diff <c>patch</c> field. GitHub omits the patch for binary files and for
/// diffs exceeding its size threshold (~3000 lines); when null, the diff provider must fall
/// back to fetching base + head content individually.
/// </summary>
public sealed record GitHubChangedFile(FileChange File, string? Patch);
