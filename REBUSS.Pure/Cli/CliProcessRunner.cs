using System.Diagnostics;
using REBUSS.Pure.Properties;

namespace REBUSS.Pure.Cli;

/// <summary>
/// Default <see cref="ICliProcessRunner"/> implementation. <see cref="Shared"/> is a
/// process-lifetime singleton — the runner is stateless, so a single instance avoids
/// per-call allocation in hot paths like the Copilot probe loop.
/// </summary>
internal sealed class CliProcessRunner : ICliProcessRunner
{
    public static readonly CliProcessRunner Shared = new();

    public async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string fileName, string arguments, CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
                return (-1, string.Empty, Resources.ErrorFailedToStartProcess);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await Task.WhenAll(stdoutTask, stderrTask);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            await process.WaitForExitAsync(cancellationToken);

            return (process.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }

    public async Task<int> RunInteractiveAsync(
        string fileName, string arguments, CancellationToken cancellationToken,
        IDictionary<string, string>? environmentOverrides = null)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                RedirectStandardInput = false
            };

            if (environmentOverrides is not null)
            {
                foreach (var (key, value) in environmentOverrides)
                    psi.Environment[key] = value;
            }

            using var process = Process.Start(psi);
            if (process is null)
                return -1;

            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}
