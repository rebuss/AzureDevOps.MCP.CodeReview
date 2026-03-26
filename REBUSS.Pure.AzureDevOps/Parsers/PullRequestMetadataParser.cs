using System.Text.Json;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.AzureDevOps.Parsers
{
    public class PullRequestMetadataParser : IPullRequestMetadataParser
    {
        private readonly ILogger<PullRequestMetadataParser> _logger;

        public PullRequestMetadataParser(ILogger<PullRequestMetadataParser> logger)
            => _logger = logger;

        public PullRequestMetadata Parse(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var title = GetString(root, "title", "Unknown");
                var status = GetString(root, "status", "Unknown");
                var sourceRefName = GetString(root, "sourceRefName", string.Empty);
                var targetRefName = GetString(root, "targetRefName", string.Empty);

                return new PullRequestMetadata(
                    title, status,
                    ExtractBranchName(sourceRefName), ExtractBranchName(targetRefName),
                    sourceRefName, targetRefName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing PR details JSON");
                return new PullRequestMetadata("Unknown", "Unknown", "Unknown", "Unknown", string.Empty, string.Empty);
            }
        }

        public FullPullRequestMetadata ParseFull(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var sourceRefName = GetString(root, "sourceRefName", string.Empty);
                var targetRefName = GetString(root, "targetRefName", string.Empty);

                return new FullPullRequestMetadata
                {
                    PullRequestId = GetInt(root, "pullRequestId", 0),
                    CodeReviewId = GetInt(root, "codeReviewId", 0),
                    Title = GetString(root, "title", "Unknown"),
                    Description = GetString(root, "description", string.Empty),
                    Status = GetString(root, "status", "Unknown"),
                    IsDraft = GetBool(root, "isDraft", false),
                    AuthorLogin = GetNestedString(root, "createdBy", "uniqueName", string.Empty),
                    AuthorDisplayName = GetNestedString(root, "createdBy", "displayName", string.Empty),
                    CreatedDate = GetDateTime(root, "creationDate", DateTime.MinValue),
                    ClosedDate = GetNullableDateTime(root, "closedDate"),
                    SourceRefName = sourceRefName,
                    TargetRefName = targetRefName,
                    SourceBranch = ExtractBranchName(sourceRefName),
                    TargetBranch = ExtractBranchName(targetRefName),
                    LastMergeSourceCommitId = GetNestedString(root, "lastMergeSourceCommit", "commitId", string.Empty),
                    LastMergeTargetCommitId = GetNestedString(root, "lastMergeTargetCommit", "commitId", string.Empty),
                    RepositoryName = GetNestedString(root, "repository", "name", string.Empty),
                    ProjectName = GetDeepNestedString(root, "repository", "project", "name", string.Empty)
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing PR details JSON for full metadata");
                return new FullPullRequestMetadata { Title = "Unknown", Status = "Unknown" };
            }
        }

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
                if (DateTime.TryParse(str, out var dt))
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
                if (DateTime.TryParse(str, out var dt))
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

        private static string ExtractBranchName(string refName)
        {
            const string prefix = "refs/heads/";
            return refName.StartsWith(prefix) ? refName[prefix.Length..] : refName;
        }
    }
}
