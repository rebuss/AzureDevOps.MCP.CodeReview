using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.AzureDevOps.Parsers
{
    public interface IIterationInfoParser
    {
        IterationInfo ParseLast(string json);
    }
}
