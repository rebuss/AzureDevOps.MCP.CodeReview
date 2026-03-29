using System.Runtime.InteropServices;
using REBUSS.Pure.AzureDevOps.Properties;

namespace REBUSS.Pure.AzureDevOps.Configuration;

/// <summary>
/// Resolves the correct <see cref="System.Diagnostics.ProcessStartInfo"/> file name and
/// arguments for running Azure CLI commands across platforms.
/// <para>
/// On Windows, <c>az</c> is a <c>.cmd</c> batch file. When <c>UseShellExecute</c> is
/// <c>false</c>, the OS does not resolve <c>.cmd</c> extensions automatically, so the
/// command must be invoked through <c>cmd.exe /c az ...</c>.
/// On Linux/macOS, <c>az</c> is a regular executable and can be called directly.
/// </para>
/// </summary>
internal static class AzureCliProcessHelper
{
    /// <summary>
    /// Returns the file name and arguments needed to run an Azure CLI command.
    /// </summary>
    /// <param name="azArguments">The Azure CLI arguments (e.g. <c>"login"</c> or
    /// <c>"account get-access-token --resource ... --output json"</c>).</param>
    /// <returns>A tuple of (<c>FileName</c>, <c>Arguments</c>) suitable for
    /// <see cref="System.Diagnostics.ProcessStartInfo"/>.</returns>
    internal static (string FileName, string Arguments) GetProcessStartArgs(string azArguments, string? azPath = null)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var azCmd = azPath is not null ? $"\"{azPath}\"" : Resources.AzCliExecutable;
            return ("cmd.exe", $"/c {azCmd} {azArguments}");
        }

        return (azPath ?? Resources.AzCliExecutable, azArguments);
    }

    /// <summary>
    /// Probes known Azure CLI installation directories on Windows and returns the full
    /// path to <c>az.cmd</c> if found, or <c>null</c> on other platforms or when not found.
    /// Useful after a fresh install where the current process PATH has not been refreshed.
    /// </summary>
    internal static string? TryFindAzCliOnWindows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return null;

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd"),
        };

        return Array.Find(candidates, File.Exists);
    }
}
