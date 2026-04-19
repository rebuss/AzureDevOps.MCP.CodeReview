namespace REBUSS.Pure.Core;

/// <summary>
/// Provides the ability to download a repository archive as a ZIP file.
/// Each SCM provider implements this to download from its own API.
/// </summary>
public interface IRepositoryArchiveProvider
{
    /// <summary>
    /// Downloads the repository source tree at a specific commit as a ZIP file
    /// and writes it to <paramref name="destinationPath"/>.
    /// The parent directory of <paramref name="destinationPath"/> must exist.
    /// </summary>
    /// <param name="commitRef">Git commit SHA to download.</param>
    /// <param name="destinationPath">Absolute path where the ZIP file should be written.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DownloadRepositoryZipAsync(string commitRef, string destinationPath, CancellationToken ct = default);
}
