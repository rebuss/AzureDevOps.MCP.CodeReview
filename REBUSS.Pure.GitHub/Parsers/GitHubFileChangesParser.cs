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

    public List<FileChange> Parse(string json) =>
        ParseWithPatches(json).Select(p => p.File).ToList();

    public List<GitHubChangedFile> ParseWithPatches(string json)
    {
        var entries = new List<GitHubChangedFile>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array)
                return entries;

            foreach (var item in root.EnumerateArray())
            {
                var entry = TryParseEntry(item);
                if (entry is not null)
                    entries.Add(entry);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing GitHub file changes JSON");
        }

        return entries;
    }

    private static GitHubChangedFile? TryParseEntry(JsonElement item)
    {
        var filename = GetString(item, "filename", string.Empty);
        if (string.IsNullOrEmpty(filename))
            return null;

        var status = GetString(item, "status", "modified");
        var file = new FileChange
        {
            Path = filename,
            ChangeType = MapStatus(status),
            Additions = GetInt(item, "additions"),
            Deletions = GetInt(item, "deletions")
        };

        var patch = item.TryGetProperty("patch", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

        return new GitHubChangedFile(file, patch);
    }

    private static string GetString(JsonElement element, string property, string fallback) =>
        element.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? fallback
            : fallback;

    private static int GetInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetInt32()
            : 0;

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
