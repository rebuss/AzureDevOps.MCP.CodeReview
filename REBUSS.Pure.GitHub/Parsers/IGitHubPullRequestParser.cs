using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.GitHub.Parsers;

public interface IGitHubPullRequestParser
{
    /// <summary>
    /// Parses lightweight PR metadata from the GitHub PR details JSON.
    /// </summary>
    PullRequestMetadata Parse(string json);

    /// <summary>
    /// Parses full PR metadata from the GitHub PR details JSON.
    /// </summary>
    FullPullRequestMetadata ParseFull(string json);

    /// <summary>
    /// Extracts the base commit SHA from the PR details JSON.
    /// </summary>
    string ParseBaseCommit(string json);

    /// <summary>
    /// Extracts the head commit SHA from the PR details JSON.
    /// </summary>
    string ParseHeadCommit(string json);
}
