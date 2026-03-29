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
    /// Handles the execution of the get_file_content_at_ref MCP tool.
    /// Validates input, delegates to <see cref="IFileContentProvider"/>,
    /// and formats the result as a structured JSON response.
    /// </summary>
    [McpServerToolType]
    public class GetFileContentAtRefToolHandler
    {
        private readonly IFileContentDataProvider _fileContentProvider;
        private readonly ILogger<GetFileContentAtRefToolHandler> _logger;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public GetFileContentAtRefToolHandler(
            IFileContentDataProvider fileContentProvider,
            ILogger<GetFileContentAtRefToolHandler> logger)
        {
            _fileContentProvider = fileContentProvider;
            _logger = logger;
        }

        [McpServerTool(Name = "get_file_content_at_ref"), Description(
            "Returns the full content of a file from the repository at a specific commit, branch, or tag. " +
            "Use this to fetch the complete file without requiring a local clone or checkout. " +
            "Typical usage: call with a commit SHA from the PR base or head to get the file before or after a change.")]
        public async Task<string> ExecuteAsync(
            [Description("The repository-relative path of the file (e.g. 'src/Cache/CacheService.cs')")] string? path = null,
            [Description("The Git ref to fetch the file at: a commit SHA (e.g. 'abc123def456'), " +
                         "a branch name (e.g. 'main'), or a tag name (e.g. 'refs/tags/v1.0')")] string? @ref = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new McpException(Resources.ErrorMissingRequiredPath);

            if (string.IsNullOrWhiteSpace(@ref))
                throw new McpException(Resources.ErrorMissingRequiredRef);

            try
            {
                _logger.LogInformation(Resources.LogGetFileContentAtRefEntry, path, @ref);
                var sw = Stopwatch.StartNew();

                var fileContent = await _fileContentProvider.GetFileContentAsync(path, @ref, cancellationToken);

                var result = new FileContentAtRefResult
                {
                    Path = fileContent.Path,
                    Ref = fileContent.Ref,
                    Size = fileContent.Size,
                    Encoding = fileContent.Encoding,
                    Content = fileContent.Content,
                    IsBinary = fileContent.IsBinary
                };

                var json = JsonSerializer.Serialize(result, JsonOptions);
                sw.Stop();

                _logger.LogInformation(
                    Resources.LogGetFileContentAtRefCompleted,
                    path, @ref, fileContent.Size, fileContent.IsBinary, json.Length, sw.ElapsedMilliseconds);

                return json;
            }
            catch (FileContentNotFoundException ex)
            {
                _logger.LogWarning(ex, Resources.LogGetFileContentAtRefNotFound,
                    path, @ref);
                throw new McpException(string.Format(Resources.ErrorFileNotFound, ex.Message));
            }
            catch (McpException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, Resources.LogGetFileContentAtRefError,
                    path, @ref);
                throw new McpException(string.Format(Resources.ErrorRetrievingFileContent, ex.Message));
            }
        }
    }
}