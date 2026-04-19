using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.GitHub.Parsers;

/// <summary>
/// Parses GitHub REST API pull request JSON responses into domain models.
/// GitHub PR JSON structure reference: https://docs.github.com/en/rest/pulls/pulls#get-a-pull-request
/// </summary>
public class GitHubPullRequestParser : IGitHubPullRequestParser
{
    private readonly ILogger<GitHubPullRequestParser> _logger;

    public GitHubPullRequestParser(ILogger<GitHubPullRequestParser> logger)
        => _logger = logger;

    public PullRequestMetadata Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var title = GetString(root, "title", "Unknown");
            var state = GetString(root, "state", "Unknown");
            var merged = GetBool(root, "merged", false);
            var sourceBranch = GetNestedString(root, "head", "ref", string.Empty);
            var targetBranch = GetNestedString(root, "base", "ref", string.Empty);

            return new PullRequestMetadata(
                title, MapState(state, merged),
                sourceBranch, targetBranch,
                $"refs/heads/{sourceBranch}", $"refs/heads/{targetBranch}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing GitHub PR details JSON");
            return new PullRequestMetadata("Unknown", "Unknown", "Unknown", "Unknown", string.Empty, string.Empty);
        }
    }

    public FullPullRequestMetadata ParseFull(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var sourceBranch = GetNestedString(root, "head", "ref", string.Empty);
            var targetBranch = GetNestedString(root, "base", "ref", string.Empty);
            var merged = GetBool(root, "merged", false);

            return new FullPullRequestMetadata
            {
                PullRequestId = GetInt(root, "number", 0),
                CodeReviewId = GetInt(root, "number", 0),
                Title = GetString(root, "title", "Unknown"),
                Description = GetString(root, "body", string.Empty),
                Status = MapState(GetString(root, "state", "Unknown"), merged),
                IsDraft = GetBool(root, "draft", false),
                AuthorLogin = GetNestedString(root, "user", "login", string.Empty),
                AuthorDisplayName = GetNestedString(root, "user", "login", string.Empty),
                CreatedDate = GetDateTime(root, "created_at", DateTime.MinValue),
                ClosedDate = GetNullableDateTime(root, "closed_at"),
                SourceRefName = $"refs/heads/{sourceBranch}",
                TargetRefName = $"refs/heads/{targetBranch}",
                SourceBranch = sourceBranch,
                TargetBranch = targetBranch,
                LastMergeSourceCommitId = GetNestedString(root, "head", "sha", string.Empty),
                LastMergeTargetCommitId = GetNestedString(root, "base", "sha", string.Empty),
                RepositoryName = GetDeepNestedString(root, "base", "repo", "name", string.Empty),
                ProjectName = string.Empty,
                Additions = GetInt(root, "additions", 0),
                Deletions = GetInt(root, "deletions", 0),
                ChangedFilesCount = GetInt(root, "changed_files", 0)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing GitHub PR details JSON for full metadata");
            return new FullPullRequestMetadata { Title = "Unknown", Status = "Unknown" };
        }
    }

    public string ParseBaseCommit(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return GetNestedString(doc.RootElement, "base", "sha", string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing base commit from GitHub PR JSON");
            return string.Empty;
        }
    }

    public string ParseHeadCommit(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return GetNestedString(doc.RootElement, "head", "sha", string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing head commit from GitHub PR JSON");
            return string.Empty;
        }
    }

    public (PullRequestMetadata Metadata, string BaseCommit, string HeadCommit) ParseWithCommits(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var title = GetString(root, "title", "Unknown");
            var state = GetString(root, "state", "Unknown");
            var merged = GetBool(root, "merged", false);
            var sourceBranch = GetNestedString(root, "head", "ref", string.Empty);
            var targetBranch = GetNestedString(root, "base", "ref", string.Empty);
            var baseCommit = GetNestedString(root, "base", "sha", string.Empty);
            var headCommit = GetNestedString(root, "head", "sha", string.Empty);

            var metadata = new PullRequestMetadata(
                title, MapState(state, merged),
                sourceBranch, targetBranch,
                $"refs/heads/{sourceBranch}", $"refs/heads/{targetBranch}");

            return (metadata, baseCommit, headCommit);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing GitHub PR details JSON with commits");
            return (new PullRequestMetadata("Unknown", "Unknown", "Unknown", "Unknown", string.Empty, string.Empty),
                    string.Empty, string.Empty);
        }
    }

    /// <summary>
    /// Maps GitHub PR state to a normalized status string.
    /// GitHub uses: "open", "closed". When the PR is closed, the <paramref name="merged"/>
    /// flag distinguishes a successful merge ("completed") from a rejected close ("abandoned").
    /// </summary>
    internal static string MapState(string state, bool merged) => state.ToLowerInvariant() switch
    {
        "open" => "active",
        "closed" when merged => "completed",
        "closed" => "abandoned",
        _ => state
    };

    private static string GetString(JsonElement element, string property, string fallback) =>
        element.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? fallback
            : fallback;

    private static int GetInt(JsonElement element, string property, int fallback) =>
        element.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetInt32()
            : fallback;

    private static bool GetBool(JsonElement element, string property, bool fallback) =>
        element.TryGetProperty(property, out var p) &&
        (p.ValueKind == JsonValueKind.True || p.ValueKind == JsonValueKind.False)
            ? p.GetBoolean()
            : fallback;

    private static DateTime GetDateTime(JsonElement element, string property, DateTime fallback)
    {
        if (element.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String)
        {
            var str = p.GetString();
            if (DateTime.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
                return dt;
        }
        return fallback;
    }

    private static DateTime? GetNullableDateTime(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var p) || p.ValueKind == JsonValueKind.Null)
            return null;

        if (p.ValueKind == JsonValueKind.String)
        {
            var str = p.GetString();
            if (DateTime.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
                return dt;
        }
        return null;
    }

    private static string GetNestedString(JsonElement element, string parentProp, string childProp, string fallback)
    {
        if (element.TryGetProperty(parentProp, out var parent) && parent.ValueKind == JsonValueKind.Object)
            return GetString(parent, childProp, fallback);
        return fallback;
    }

    private static string GetDeepNestedString(
        JsonElement element, string prop1, string prop2, string prop3, string fallback)
    {
        if (element.TryGetProperty(prop1, out var level1) && level1.ValueKind == JsonValueKind.Object &&
            level1.TryGetProperty(prop2, out var level2) && level2.ValueKind == JsonValueKind.Object)
            return GetString(level2, prop3, fallback);
        return fallback;
    }
}
