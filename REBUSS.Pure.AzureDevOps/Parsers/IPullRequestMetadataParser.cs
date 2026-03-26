using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.AzureDevOps.Parsers
{
    public interface IPullRequestMetadataParser
    {
        PullRequestMetadata Parse(string json);
        FullPullRequestMetadata ParseFull(string json);
    }
}
