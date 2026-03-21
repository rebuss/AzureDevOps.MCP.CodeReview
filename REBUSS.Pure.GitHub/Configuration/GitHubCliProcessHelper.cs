using System.Runtime.InteropServices;

namespace REBUSS.Pure.GitHub.Configuration;

/// <summary>
/// Resolves the correct <see cref="System.Diagnostics.ProcessStartInfo"/> file name and
/// arguments for running GitHub CLI commands across platforms.
/// <para>
/// On Windows, <c>gh</c> is typically installed via <c>winget</c> or <c>scoop</c> and may
/// not be directly resolvable when <c>UseShellExecute</c> is <c>false</c>, so the command
/// is invoked through <c>cmd.exe /c gh ...</c>.
/// On Linux/macOS, <c>gh</c> is a regular executable and can be called directly.
/// </para>
/// </summary>
internal static class GitHubCliProcessHelper
{
    /// <summary>
    /// Returns the file name and arguments needed to run a GitHub CLI command.
    /// </summary>
    /// <param name="ghArguments">The GitHub CLI arguments (e.g. <c>"auth token"</c> or
    /// <c>"auth login --web"</c>).</param>
    /// <param name="ghPath">Optional full path to the <c>gh</c> executable.</param>
    /// <returns>A tuple of (<c>FileName</c>, <c>Arguments</c>) suitable for
    /// <see cref="System.Diagnostics.ProcessStartInfo"/>.</returns>
    internal static (string FileName, string Arguments) GetProcessStartArgs(string ghArguments, string? ghPath = null)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var ghCmd = ghPath is not null ? $"\"{ghPath}\"" : "gh";
            return ("cmd.exe", $"/c {ghCmd} {ghArguments}");
        }

        return (ghPath ?? "gh", ghArguments);
    }

    /// <summary>
    /// Probes known GitHub CLI installation directories on Windows and returns the full
    /// path to <c>gh.exe</c> if found, or <c>null</c> on other platforms or when not found.
    /// Useful after a fresh install where the current process PATH has not been refreshed.
    /// </summary>
    internal static string? TryFindGhCliOnWindows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return null;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        var candidates = new[]
        {
            Path.Combine(programFiles, "GitHub CLI", "gh.exe"),
            Path.Combine(localAppData, "Microsoft", "WinGet", "Packages", "GitHub.cli_Microsoft.Winget.Source_8wekyb3d8bbwe", "gh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims", "gh.exe"),
        };

        return Array.Find(candidates, File.Exists);
    }
}
