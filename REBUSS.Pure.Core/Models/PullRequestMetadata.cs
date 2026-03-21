namespace REBUSS.Pure.Core.Models
{
    /// <summary>
    /// Lightweight parsed metadata from a pull request details endpoint.
    /// </summary>
    public sealed record PullRequestMetadata(
        string Title,
        string Status,
        string SourceBranch,
        string TargetBranch,
        string SourceRefName,
        string TargetRefName);
}
