using System.Diagnostics;
using System.Text;

namespace REBUSS.Pure.SmokeTests.Fixtures;

/// <summary>
/// Runs <c>rebuss-pure</c> CLI commands (e.g. <c>init</c>) as a child process
/// and captures stdout, stderr, and exit code.
/// </summary>
public sealed record CliProcessResult(int ExitCode, string StdOut, string StdErr);

public static class CliProcessHelper
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Runs <c>dotnet run --project REBUSS.Pure -- {arguments}</c> with the given working directory.
    /// Pipes <paramref name="stdin"/> into the process (useful for answering auth prompts).
    /// </summary>
    public static async Task<CliProcessResult> RunAsync(
        string workingDirectory,
        string arguments,
        string? stdin = null,
        TimeSpan? timeout = null,
        Dictionary<string, string>? environmentOverrides = null)
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;
        var projectDir = ResolveProjectDirectory();

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{projectDir}\" --no-launch-profile -- {arguments}",
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (environmentOverrides is not null)
        {
            foreach (var (key, value) in environmentOverrides)
                psi.Environment[key] = value;
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start CLI process.");

        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin);
            await process.StandardInput.FlushAsync();
        }
        process.StandardInput.Close();

        using var cts = new CancellationTokenSource(effectiveTimeout);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return new CliProcessResult(-1, string.Empty, "Process timed out.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return new CliProcessResult(process.ExitCode, stdout, stderr);
    }

    private static string ResolveProjectDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "REBUSS.Pure", "REBUSS.Pure.csproj");
            if (File.Exists(candidate))
                return Path.Combine(dir.FullName, "REBUSS.Pure");

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate REBUSS.Pure project. Ensure the solution structure is intact.");
    }

    /// <summary>
    /// Builds a restricted PATH that includes only <c>dotnet</c> and <c>git</c> directories,
    /// effectively hiding <c>gh</c>, <c>az</c>, and other CLI tools.
    /// Use this for tests that must exercise the "CLI not installed" auth flow.
    /// </summary>
    public static Dictionary<string, string> BuildRestrictedPathEnv()
    {
        var pathSeparator = Path.PathSeparator;
        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var dirs = currentPath.Split(pathSeparator);

        var allowed = dirs.Where(d =>
        {
            var lower = d.ToLowerInvariant();
            return lower.Contains("dotnet") || lower.Contains("git") ||
                   lower.Contains("system32") || lower.Contains("usr");
        });

        return new Dictionary<string, string>
        {
            ["PATH"] = string.Join(pathSeparator, allowed)
        };
    }
}
