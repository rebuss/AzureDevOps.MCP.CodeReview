namespace REBUSS.Pure.Core.Models;

/// <summary>
/// Represents the current phase of a repository download lifecycle.
/// </summary>
public enum DownloadStatus
{
    NotStarted = 0,
    Downloading = 1,
    Extracting = 2,
    Ready = 3,
    Failed = 4
}

/// <summary>
/// Tracks the state of a repository download for a given server session.
/// </summary>
public class RepositoryDownloadState
{
    public DownloadStatus Status { get; set; } = DownloadStatus.NotStarted;
    public string? ExtractedPath { get; set; }
    public string? ErrorMessage { get; set; }
    public int PrNumber { get; set; }
    public string CommitRef { get; set; } = string.Empty;
    public Task DownloadTask { get; set; } = Task.CompletedTask;
}
