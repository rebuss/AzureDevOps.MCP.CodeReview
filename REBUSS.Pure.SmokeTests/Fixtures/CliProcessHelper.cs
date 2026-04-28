using System.Diagnostics;
using System.Runtime.Versioning;
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

#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif

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
            Arguments = $"run --project \"{projectDir}\" -c {BuildConfiguration} --no-build --no-launch-profile -- {arguments}",
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
            try
            {
                await process.StandardInput.WriteAsync(stdin);
                await process.StandardInput.FlushAsync();
            }
            catch (IOException)
            {
                // Process may have exited before consuming stdin — this is expected
                // for commands that finish without reading interactive input.
            }
        }

        try { process.StandardInput.Close(); }
        catch (IOException) { }

        // Start pipe reads immediately to prevent the process from blocking
        // when output exceeds the OS pipe buffer size.
        // Use a SEPARATE CTS for pipe reads — not tied to the process exit timeout.
        using var pipeCts = new CancellationTokenSource();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(pipeCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(pipeCts.Token);

        // WaitForExitAsync in .NET 7+ also waits for redirected-pipe EOF,
        // which can hang when child processes inherit and hold pipe handles.
        // Use WaitForExit(TimeSpan) to wait for the process exit signal only.
        var exited = await Task.Run(() => process.WaitForExit(effectiveTimeout));

        if (!exited)
        {
            process.Kill(entireProcessTree: true);
            pipeCts.Cancel();
            return new CliProcessResult(-1, string.Empty, "Process timed out.");
        }

        // Process exited — drain pipes with a grace period.
        // If child processes keep handles open, reads will hang until cancelled.
        pipeCts.CancelAfter(TimeSpan.FromSeconds(5));
        string stdout, stderr;
        try
        {
            stdout = await stdoutTask;
            stderr = await stderrTask;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            stdout = string.Empty;
            stderr = string.Empty;
        }

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
    /// Builds environment overrides that hide <c>gh</c>, <c>az</c>, and other CLI tools,
    /// keeping only <c>dotnet</c> and <c>git</c> accessible.
    /// Use this for tests that must exercise the "CLI not installed" auth flow.
    /// <para>
    /// On Windows, PATH directories are filtered to include only those containing
    /// <c>dotnet</c>, <c>git</c>, or <c>system32</c>.
    /// On Linux/macOS, tools like <c>gh</c> and <c>az</c> share <c>/usr/bin</c> with
    /// <c>git</c>, so PATH filtering alone cannot hide them. Instead, a temporary
    /// directory with shadow scripts (that always exit with code 1) is prepended to
    /// PATH, shadowing the real <c>gh</c>/<c>az</c> binaries.
    /// </para>
    /// <para>
    /// Dispose the returned <see cref="RestrictedPathEnv"/> to clean up the shadow directory.
    /// </para>
    /// </summary>
    public static RestrictedPathEnv BuildRestrictedPathEnv()
    {
        if (OperatingSystem.IsWindows())
            return new RestrictedPathEnv(BuildRestrictedPathEnvWindows(), shadowDir: null);

        return BuildRestrictedPathEnvUnix();
    }

    private static Dictionary<string, string> BuildRestrictedPathEnvWindows()
    {
        var pathSeparator = Path.PathSeparator;
        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var dirs = currentPath.Split(pathSeparator);

        var allowed = dirs.Where(d =>
        {
            var lower = d.ToLowerInvariant();
            return lower.Contains("dotnet") || lower.Contains("git") ||
                   lower.Contains("system32");
        });

        return new Dictionary<string, string>
        {
            ["PATH"] = string.Join(pathSeparator, allowed)
        };
    }

    [UnsupportedOSPlatform("windows")]
    private static RestrictedPathEnv BuildRestrictedPathEnvUnix()
    {
        var shadowBin = Path.Combine(
            Path.GetTempPath(),
            "rebuss-smoke-shadow-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(shadowBin);

        foreach (var tool in new[] { "gh", "az" })
        {
            var scriptPath = Path.Combine(shadowBin, tool);
            File.WriteAllText(scriptPath, "#!/bin/sh\nexit 1\n");
            File.SetUnixFileMode(scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        var env = new Dictionary<string, string>
        {
            ["PATH"] = shadowBin + Path.PathSeparator + currentPath
        };

        return new RestrictedPathEnv(env, shadowBin);
    }

    /// <summary>
    /// Holds restricted PATH environment overrides and cleans up any shadow directory on dispose.
    /// </summary>
    public sealed class RestrictedPathEnv : IDisposable
    {
        private readonly string? _shadowDir;

        public Dictionary<string, string> Env { get; }

        public RestrictedPathEnv(Dictionary<string, string> env, string? shadowDir)
        {
            Env = env;
            _shadowDir = shadowDir;
        }

        public void Dispose()
        {
            if (_shadowDir is not null)
            {
                try { Directory.Delete(_shadowDir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
