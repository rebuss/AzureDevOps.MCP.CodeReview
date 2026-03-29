using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Properties;
using REBUSS.Pure.Tools.Models;
using System.Text.Json;

namespace REBUSS.Pure.Tools
{
    /// <summary>
    /// Handles the execution of the get_file_diff MCP tool.
    /// Validates input, delegates to <see cref="IPullRequestDiffProvider"/>,
    /// and returns a structured JSON result with per-file hunks for a single file.
    /// </summary>
    [McpServerToolType]
    public class GetFileDiffToolHandler
    {
        private readonly IPullRequestDataProvider _diffProvider;
        private readonly ILogger<GetFileDiffToolHandler> _logger;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public GetFileDiffToolHandler(
            IPullRequestDataProvider diffProvider,
            ILogger<GetFileDiffToolHandler> logger)
        {
            _diffProvider = diffProvider;
            _logger = logger;
        }

        [McpServerTool(Name = "get_file_diff"), Description(
            "Retrieves the diff for a single file in a specific Pull Request. " +
            "Returns a structured JSON object with the file diff including hunks optimized for AI code review.")]
        public async Task<string> ExecuteAsync(
            [Description("The Pull Request number/ID to retrieve the diff for")] int? prNumber = null,
            [Description("The repository-relative path of the file (e.g. 'src/Cache/CacheService.cs')")] string? path = null,
            CancellationToken cancellationToken = default)
        {
            if (prNumber != null && prNumber <= 0)
                throw new McpException(Resources.ErrorPrNumberMustBePositive);

            if (prNumber == null)
                throw new McpException(Resources.ErrorMissingRequiredPrNumber);

            if (string.IsNullOrWhiteSpace(path))
                throw new McpException(Resources.ErrorMissingRequiredPath);

            try
            {
                _logger.LogInformation(Resources.LogGetFileDiffEntry,
                    prNumber, path);
                var sw = Stopwatch.StartNew();

                var diff = await _diffProvider.GetFileDiffAsync(prNumber.Value, path, cancellationToken);

                var json = BuildStructuredResult(prNumber.Value, diff);

                sw.Stop();

                _logger.LogInformation(
                    Resources.LogGetFileDiffCompleted,
                    prNumber, path, json.Length, sw.ElapsedMilliseconds);

                return json;
            }
            catch (PullRequestNotFoundException ex)
            {
                _logger.LogWarning(ex, Resources.LogGetFileDiffPrNotFound,
                    prNumber, path);
                throw new McpException(string.Format(Resources.ErrorPullRequestNotFound, ex.Message));
            }
            catch (FileNotFoundInPullRequestException ex)
            {
                _logger.LogWarning(ex, Resources.LogGetFileDiffFileNotFound,
                    prNumber, path);
                throw new McpException(string.Format(Resources.ErrorFileNotFoundInPullRequest, ex.Message));
            }
            catch (McpException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, Resources.LogGetFileDiffError,
                    prNumber, path);
                throw new McpException(string.Format(Resources.ErrorRetrievingFileDiff, ex.Message));
            }
        }

        // --- Result builder -------------------------------------------------------

        private static string BuildStructuredResult(int prNumber, PullRequestDiff diff)
        {
            var structured = new StructuredDiffResult
            {
                PrNumber = prNumber,
                Files = diff.Files.Select(f => new StructuredFileChange
                {
                    Path = f.Path,
                    ChangeType = f.ChangeType,
                    SkipReason = f.SkipReason,
                    Additions = f.Additions,
                    Deletions = f.Deletions,
                    Hunks = f.Hunks.Select(h => new StructuredHunk
                    {
                        OldStart = h.OldStart,
                        OldCount = h.OldCount,
                        NewStart = h.NewStart,
                        NewCount = h.NewCount,
                        Lines = h.Lines.Select(l => new StructuredLine
                        {
                            Op = l.Op.ToString(),
                            Text = l.Text
                        }).ToList()
                    }).ToList()
                }).ToList()
            };

            return JsonSerializer.Serialize(structured, JsonOptions);
        }
    }
}