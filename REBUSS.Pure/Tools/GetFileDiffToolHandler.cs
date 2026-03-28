using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Core.Models;
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
                throw new McpException("prNumber must be greater than 0");

            if (prNumber == null)
                throw new McpException("Missing required parameter: prNumber");

            if (string.IsNullOrWhiteSpace(path))
                throw new McpException("Missing required parameter: path");

            try
            {
                _logger.LogInformation("[get_file_diff] Entry: PR #{PrNumber}, path='{Path}'",
                    prNumber, path);
                var sw = Stopwatch.StartNew();

                var diff = await _diffProvider.GetFileDiffAsync(prNumber.Value, path, cancellationToken);

                var json = BuildStructuredResult(prNumber.Value, diff);

                sw.Stop();

                _logger.LogInformation(
                    "[get_file_diff] Completed: PR #{PrNumber}, path='{Path}', {ResponseLength} chars, {ElapsedMs}ms",
                    prNumber, path, json.Length, sw.ElapsedMilliseconds);

                return json;
            }
            catch (PullRequestNotFoundException ex)
            {
                _logger.LogWarning(ex, "[get_file_diff] Pull request not found (prNumber={PrNumber}, path='{Path}')",
                    prNumber, path);
                throw new McpException($"Pull Request not found: {ex.Message}");
            }
            catch (FileNotFoundInPullRequestException ex)
            {
                _logger.LogWarning(ex, "[get_file_diff] File not found in pull request (prNumber={PrNumber}, path='{Path}')",
                    prNumber, path);
                throw new McpException($"File not found in Pull Request: {ex.Message}");
            }
            catch (McpException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[get_file_diff] Error (prNumber={PrNumber}, path='{Path}')",
                    prNumber, path);
                throw new McpException($"Error retrieving file diff: {ex.Message}");
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