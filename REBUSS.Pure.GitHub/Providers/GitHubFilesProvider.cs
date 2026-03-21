using System.Diagnostics;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Shared;

namespace REBUSS.Pure.GitHub.Providers;

/// <summary>
/// Fetches the list of changed files for a pull request, classifies each file,
/// computes per-file line stats from the diff content, and builds a category summary.
/// Delegates to <see cref="GitHubDiffProvider"/> for the raw file data and diffs.
/// </summary>
public class GitHubFilesProvider
{
    private readonly GitHubDiffProvider _diffProvider;
    private readonly IFileClassifier _fileClassifier;
    private readonly ILogger<GitHubFilesProvider> _logger;

    public GitHubFilesProvider(
        GitHubDiffProvider diffProvider,
        IFileClassifier fileClassifier,
        ILogger<GitHubFilesProvider> logger)
    {
        _diffProvider = diffProvider;
        _fileClassifier = fileClassifier;
        _logger = logger;
    }

    public virtual async Task<PullRequestFiles> GetFilesAsync(int prNumber, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching files for PR #{PrNumber}", prNumber);
        var sw = Stopwatch.StartNew();

        var diff = await _diffProvider.GetDiffAsync(prNumber, cancellationToken);

        var classified = diff.Files
            .Select(f => (fileChange: f, classification: _fileClassifier.Classify(f.Path)))
            .ToList();

        var files = classified.Select(x => BuildFileInfo(x.fileChange, x.classification)).ToList();
        var summary = BuildSummary(classified.Select(x => x.classification).ToList(), files);

        sw.Stop();

        _logger.LogInformation(
            "Files for PR #{PrNumber} completed: {TotalFiles} file(s) " +
            "(source={SourceFiles}, test={TestFiles}, config={ConfigFiles}, docs={DocsFiles}, " +
            "binary={BinaryFiles}, generated={GeneratedFiles}, highPriority={HighPriority}), {ElapsedMs}ms",
            prNumber, files.Count,
            summary.SourceFiles, summary.TestFiles, summary.ConfigFiles, summary.DocsFiles,
            summary.BinaryFiles, summary.GeneratedFiles, summary.HighPriorityFiles,
            sw.ElapsedMilliseconds);

        return new PullRequestFiles { Files = files, Summary = summary };
    }

    private static PullRequestFileInfo BuildFileInfo(FileChange fileChange, FileClassification classification)
    {
        return new PullRequestFileInfo
        {
            Path = fileChange.Path.TrimStart('/'),
            Status = MapStatus(fileChange.ChangeType),
            Additions = fileChange.Additions,
            Deletions = fileChange.Deletions,
            Changes = fileChange.Additions + fileChange.Deletions,
            Extension = classification.Extension,
            IsBinary = classification.IsBinary,
            IsGenerated = classification.IsGenerated,
            IsTestFile = classification.IsTestFile,
            ReviewPriority = classification.ReviewPriority
        };
    }

    private static string MapStatus(string changeType) => changeType.ToLowerInvariant() switch
    {
        "add" => "added",
        "edit" => "modified",
        "delete" => "removed",
        "rename" => "renamed",
        _ => changeType
    };

    private static PullRequestFilesSummary BuildSummary(
        List<FileClassification> classifications, List<PullRequestFileInfo> files)
    {
        return new PullRequestFilesSummary
        {
            SourceFiles = classifications.Count(c => c.Category == FileCategory.Source),
            TestFiles = classifications.Count(c => c.Category == FileCategory.Test),
            ConfigFiles = classifications.Count(c => c.Category == FileCategory.Config),
            DocsFiles = classifications.Count(c => c.Category == FileCategory.Docs),
            BinaryFiles = classifications.Count(c => c.Category == FileCategory.Binary),
            GeneratedFiles = classifications.Count(c => c.Category == FileCategory.Generated),
            HighPriorityFiles = files.Count(f => f.ReviewPriority == "high")
        };
    }
}
