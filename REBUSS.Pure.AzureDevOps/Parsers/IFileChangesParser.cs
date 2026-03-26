using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.AzureDevOps.Parsers
{
    public interface IFileChangesParser
    {
        List<FileChange> Parse(string json);
    }
}
