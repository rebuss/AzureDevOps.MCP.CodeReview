using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.GitHub.Parsers;

public interface IGitHubFileChangesParser
{
    List<FileChange> Parse(string json);
}
