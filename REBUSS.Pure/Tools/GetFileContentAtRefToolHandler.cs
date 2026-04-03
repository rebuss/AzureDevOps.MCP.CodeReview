using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Properties;
using REBUSS.Pure.Tools.Shared;

namespace REBUSS.Pure.Tools
{
    /// <summary>
    /// Handles the execution of the get_file_content_at_ref MCP tool.
    /// Validates input, delegates to <see cref="IFileContentDataProvider"/>,
    /// and formats the result as a plain-text response.
    /// </summary>
    [McpServerToolType]
    public class GetFileContentAtRefToolHandler
    {
        private readonly IFileContentDataProvider _fileContentProvider;
        private readonly ILogger<GetFileContentAtRefToolHandler> _logger;

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
        public async Task<IEnumerable<ContentBlock>> ExecuteAsync(
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
                var text = PlainTextFormatter.FormatFileContent(fileContent);
                sw.Stop();

                _logger.LogInformation(
                    Resources.LogGetFileContentAtRefCompleted,
                    path, @ref, fileContent.Size, fileContent.IsBinary, text.Length, sw.ElapsedMilliseconds);

                return [new TextContentBlock { Text = text }];
            }
            catch (FileContentNotFoundException ex)
            {
                _logger.LogWarning(ex, Resources.LogGetFileContentAtRefNotFound, path, @ref);
                throw new McpException(string.Format(Resources.ErrorFileNotFound, ex.Message));
            }
            catch (McpException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, Resources.LogGetFileContentAtRefError, path, @ref);
                throw new McpException(string.Format(Resources.ErrorRetrievingFileContent, ex.Message));
            }
        }
    }
}