using System.Text.Json;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.GitHub.Parsers;

/// <summary>
/// Parses the GitHub REST API pull request files endpoint response.
/// GitHub returns a JSON array of file objects with "filename", "status", "additions", "deletions".
/// </summary>
public class GitHubFileChangesParser : IGitHubFileChangesParser
{
    private readonly ILogger<GitHubFileChangesParser> _logger;

    public GitHubFileChangesParser(ILogger<GitHubFileChangesParser> logger)
        => _logger = logger;

    public List<FileChange> Parse(string json)
    {
        var files = new List<FileChange>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array)
                return files;

            foreach (var item in root.EnumerateArray())
            {
                var file = TryParseEntry(item);
                if (file is not null)
                    files.Add(file);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing GitHub file changes JSON");
        }

        return files;
    }

    private static FileChange? TryParseEntry(JsonElement item)
    {
        var filename = GetString(item, "filename", string.Empty);
        if (string.IsNullOrEmpty(filename))
            return null;

        var status = GetString(item, "status", "modified");
        return new FileChange { Path = filename, ChangeType = MapStatus(status) };
    }

    private static string GetString(JsonElement element, string property, string fallback) =>
        element.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? fallback
            : fallback;

    /// <summary>
    /// Maps GitHub file status to the normalized change type used in domain models.
    /// GitHub uses: "added", "removed", "modified", "renamed", "copied", "changed", "unchanged".
    /// </summary>
    internal static string MapStatus(string status) => status.ToLowerInvariant() switch
    {
        "added" => "add",
        "removed" => "delete",
        "modified" => "edit",
        "renamed" => "rename",
        "copied" => "add",
        "changed" => "edit",
        _ => status
    };
}
