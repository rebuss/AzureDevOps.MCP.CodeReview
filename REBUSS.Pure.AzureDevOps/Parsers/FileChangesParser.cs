using System.Text.Json;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.AzureDevOps.Parsers
{
    public class FileChangesParser : IFileChangesParser
    {
        private readonly ILogger<FileChangesParser> _logger;

        public FileChangesParser(ILogger<FileChangesParser> logger)
            => _logger = logger;

        public List<FileChange> Parse(string json)
        {
            var files = new List<FileChange>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!TryGetEntries(root, out var entries))
                    return files;

                foreach (var change in entries.EnumerateArray())
                {
                    var file = TryParseEntry(change);
                    if (file is not null)
                        files.Add(file);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing file changes JSON");
            }

            return files;
        }

        private static bool TryGetEntries(JsonElement root, out JsonElement entries)
        {
            if (root.TryGetProperty("changeEntries", out entries) && entries.ValueKind == JsonValueKind.Array)
                return true;

            if (root.TryGetProperty("changes", out entries) && entries.ValueKind == JsonValueKind.Array)
                return true;

            entries = default;
            return false;
        }

        private static FileChange? TryParseEntry(JsonElement change)
        {
            if (!change.TryGetProperty("item", out var item))
                return null;

            if (item.TryGetProperty("isFolder", out var isFolder) && isFolder.GetBoolean())
                return null;

            var path = GetString(item, "path", string.Empty);
            if (string.IsNullOrEmpty(path))
                return null;

            var changeType = GetString(change, "changeType", "edit");
            return new FileChange { Path = path, ChangeType = MapChangeType(changeType) };
        }

        private static string GetString(JsonElement element, string property, string fallback) =>
            element.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString() ?? fallback
                : fallback;

        private static string MapChangeType(string changeType) => changeType.ToLowerInvariant() switch
        {
            "add" => "add",
            "edit" => "edit",
            "delete" => "delete",
            "rename" => "rename",
            _ => changeType
        };
    }
}
