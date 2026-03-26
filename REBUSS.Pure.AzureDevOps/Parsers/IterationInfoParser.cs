using System.Text.Json;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.AzureDevOps.Parsers
{
    /// <summary>
    /// Parses the Azure DevOps iterations endpoint response and extracts
    /// the last iteration's ID and commit SHAs.
    ///
    /// baseCommit   = commonRefCommit  (merge-base; best diff base)
    /// targetCommit = sourceRefCommit  (HEAD of the feature branch)
    /// </summary>
    public class IterationInfoParser : IIterationInfoParser
    {
        private readonly ILogger<IterationInfoParser> _logger;

        public IterationInfoParser(ILogger<IterationInfoParser> logger)
            => _logger = logger;

        public IterationInfo ParseLast(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
                    return IterationInfo.Empty;

                JsonElement? last = null;
                foreach (var item in values.EnumerateArray())
                    last = item;

                if (last is null)
                    return IterationInfo.Empty;

                var id = last.Value.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;
                var targetCommit = ExtractNestedCommitId(last.Value, "sourceRefCommit");
                var baseCommit = ExtractNestedCommitId(last.Value, "commonRefCommit");

                if (string.IsNullOrEmpty(baseCommit))
                    baseCommit = ExtractNestedCommitId(last.Value, "targetRefCommit");

                _logger.LogInformation(
                    "Last iteration #{Id}: baseCommit={Base} targetCommit={Target}",
                    id, baseCommit, targetCommit);

                return new IterationInfo(id, baseCommit, targetCommit);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing iterations JSON");
                return IterationInfo.Empty;
            }
        }

        private static string ExtractNestedCommitId(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var prop))
                return string.Empty;

            return prop.TryGetProperty("commitId", out var commitId) && commitId.ValueKind == JsonValueKind.String
                ? commitId.GetString() ?? string.Empty
                : string.Empty;
        }
    }
}
