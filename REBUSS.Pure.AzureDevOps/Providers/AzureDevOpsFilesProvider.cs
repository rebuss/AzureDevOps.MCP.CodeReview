using System.Diagnostics;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.AzureDevOps.Api;
using REBUSS.Pure.AzureDevOps.Parsers;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Shared;

namespace REBUSS.Pure.AzureDevOps.Providers
{
    /// <summary>
    /// Fetches the list of changed files for a pull request, classifies each file,
    /// and builds a category summary. Calls the Azure DevOps API directly to retrieve
    /// file metadata from iteration changes without fetching individual file contents.
    /// <para>
    /// <b>Line-count limitation:</b> <see cref="Core.Models.PullRequestFileInfo.Additions"/>,
    /// <see cref="Core.Models.PullRequestFileInfo.Deletions"/>, and
    /// <see cref="Core.Models.PullRequestFileInfo.Changes"/> are always zero because the Azure DevOps
    /// iteration-changes API does not include per-file line counts. Consumers must treat these
    /// zero values as "unavailable" rather than "no lines changed" (see
    /// <see cref="Core.IPullRequestDataProvider.GetFilesAsync"/> remarks).
    /// </para>
    /// </summary>
    public class AzureDevOpsFilesProvider
    {
        private readonly IAzureDevOpsApiClient _apiClient;
        private readonly IFileChangesParser _fileChangesParser;
        private readonly IIterationInfoParser _iterationInfoParser;
        private readonly IFileClassifier _fileClassifier;
        private readonly ILogger<AzureDevOpsFilesProvider> _logger;

        public AzureDevOpsFilesProvider(
            IAzureDevOpsApiClient apiClient,
            IFileChangesParser fileChangesParser,
            IIterationInfoParser iterationInfoParser,
            IFileClassifier fileClassifier,
            ILogger<AzureDevOpsFilesProvider> logger)
        {
            _apiClient = apiClient;
            _fileChangesParser = fileChangesParser;
            _iterationInfoParser = iterationInfoParser;
            _fileClassifier = fileClassifier;
            _logger = logger;
        }

        public virtual async Task<PullRequestFiles> GetFilesAsync(int prNumber, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Fetching files for PR #{PrNumber}", prNumber);
            var sw = Stopwatch.StartNew();

            var iterationsJson = await _apiClient.GetPullRequestIterationsAsync(prNumber);
            var lastIteration = _iterationInfoParser.ParseLast(iterationsJson);

            if (lastIteration == IterationInfo.Empty)
            {
                _logger.LogWarning("No iterations found for PR #{PrNumber}", prNumber);
                return new PullRequestFiles
                {
                    Files = new List<PullRequestFileInfo>(),
                    Summary = new PullRequestFilesSummary()
                };
            }

            var changesJson = await _apiClient.GetPullRequestIterationChangesAsync(prNumber, lastIteration.Id);
            var fileChanges = _fileChangesParser.Parse(changesJson);

            var classified = fileChanges
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
}
