using System.Diagnostics;

namespace REBUSS.Pure.SmokeTests.Fixtures;

/// <summary>
/// Creates a temporary directory with <c>git init</c> and an optional remote.
/// Disposes by deleting the entire directory tree.
/// </summary>
public sealed class TempGitRepoFixture : IDisposable
{
    public string RootPath { get; }

    private TempGitRepoFixture(string rootPath)
    {
        RootPath = rootPath;
    }

    /// <summary>
    /// Creates a temp git repository with the specified remote URL.
    /// </summary>
    public static TempGitRepoFixture Create(string? remoteUrl = null)
    {
        var path = Path.Combine(Path.GetTempPath(), "rebuss-smoke-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(path);

        RunGit(path, "init");
        RunGit(path, "config user.email smoke@test.local");
        RunGit(path, "config user.name SmokeTest");

        if (!string.IsNullOrEmpty(remoteUrl))
            RunGit(path, $"remote add origin {remoteUrl}");

        return new TempGitRepoFixture(path);
    }

    /// <summary>
    /// Creates a temp directory that is NOT a git repository.
    /// </summary>
    public static TempGitRepoFixture CreateNonGitDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "rebuss-smoke-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(path);
        return new TempGitRepoFixture(path);
    }

    /// <summary>
    /// Creates a marker folder (e.g. <c>.vscode</c>, <c>.vs</c>) inside the repo root.
    /// </summary>
    public void CreateDirectory(string relativePath)
    {
        Directory.CreateDirectory(Path.Combine(RootPath, relativePath));
    }

    /// <summary>
    /// Creates a file inside the repo root with the given content.
    /// </summary>
    public void CreateFile(string relativePath, string content = "")
    {
        var fullPath = Path.Combine(RootPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    public bool FileExists(string relativePath) =>
        File.Exists(Path.Combine(RootPath, relativePath));

    public string ReadFile(string relativePath) =>
        File.ReadAllText(Path.Combine(RootPath, relativePath));

    public void Dispose()
    {
        try
        {
            Directory.Delete(RootPath, recursive: true);
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    private static void RunGit(string workingDirectory, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start: git {arguments}");

        process.WaitForExit(TimeSpan.FromSeconds(10));

        if (process.ExitCode != 0)
        {
            var stderr = process.StandardError.ReadToEnd();
            throw new InvalidOperationException($"git {arguments} failed (exit {process.ExitCode}): {stderr}");
        }
    }
}
