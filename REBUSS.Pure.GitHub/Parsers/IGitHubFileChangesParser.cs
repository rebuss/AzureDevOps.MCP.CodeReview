using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.GitHub.Parsers;

public interface IGitHubFileChangesParser
{
    List<FileChange> Parse(string json);

    /// <summary>
    /// Parses the response and pairs each <see cref="FileChange"/> with its optional
    /// unified-diff <c>patch</c>. The patch is null for binary files and for diffs that
    /// exceed GitHub's size threshold; consumers should treat null as "fall back to
    /// per-file content fetch".
    /// </summary>
    List<GitHubChangedFile> ParseWithPatches(string json);
}
