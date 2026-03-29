using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.AzureDevOps.Api;
using REBUSS.Pure.AzureDevOps.Properties;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.AzureDevOps.Providers
{
    /// <summary>
    /// Fetches the full content of a file from an Azure DevOps repository at a specific Git ref.
    /// Delegates to <see cref="IAzureDevOpsApiClient"/> for the raw API call.
    /// </summary>
    public class AzureDevOpsFileContentProvider
    {
        private readonly IAzureDevOpsApiClient _apiClient;
        private readonly ILogger<AzureDevOpsFileContentProvider> _logger;

        public AzureDevOpsFileContentProvider(
            IAzureDevOpsApiClient apiClient,
            ILogger<AzureDevOpsFileContentProvider> logger)
        {
            _apiClient = apiClient;
            _logger = logger;
        }

        public virtual async Task<FileContent> GetFileContentAsync(
            string path, string gitRef, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Fetching content for '{Path}' at ref '{Ref}'", path, gitRef);

            var sw = Stopwatch.StartNew();
            cancellationToken.ThrowIfCancellationRequested();

            var content = await _apiClient.GetFileContentAtRefAsync(gitRef, path);

            if (content == null)
            {
                throw new FileContentNotFoundException(
                    string.Format(Resources.ErrorFileNotFoundAtRef, path, gitRef));
            }

            var isBinary = IsBinaryContent(content);
            var size = Encoding.UTF8.GetByteCount(content);

            sw.Stop();

            _logger.LogInformation(
                "File content for '{Path}' at ref '{Ref}' completed: {Size} bytes, binary={IsBinary}, {ElapsedMs}ms",
                path, gitRef, size, isBinary, sw.ElapsedMilliseconds);

            return new FileContent
            {
                Path = path.TrimStart('/'),
                Ref = gitRef,
                Size = size,
                Encoding = isBinary ? "base64" : "utf-8",
                Content = isBinary ? Convert.ToBase64String(Encoding.UTF8.GetBytes(content)) : content,
                IsBinary = isBinary
            };
        }

        private static bool IsBinaryContent(string content)
        {
            return content.Contains('\0');
        }
    }
}
