using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace REBUSS.Pure.Services.ClaudeCode;

/// <summary>
/// Runs <c>claude -p "ping" --output-format json --bare</c> under a short timeout
/// and parses the resulting JSON to decide whether the Claude Code CLI is wired up
/// correctly. Does not retain any conversation state — the call is purely diagnostic.
/// </summary>
public sealed class ClaudeVerificationRunner : IClaudeVerificationProbe
{
    private const int DefaultTimeoutSeconds = 30;

    private readonly ILogger<ClaudeVerificationRunner>? _logger;
    private readonly Func<string, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>>? _processRunner;
    private readonly string _claudeExe;

    public ClaudeVerificationRunner(
        ILogger<ClaudeVerificationRunner>? logger = null,
        Func<string, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>>? processRunner = null,
        string? claudeCliPathOverride = null)
    {
        _logger = logger;
        _processRunner = processRunner;
        _claudeExe = string.IsNullOrWhiteSpace(claudeCliPathOverride) ? "claude" : claudeCliPathOverride;
    }

    public async Task<ClaudeVerdict> ProbeAsync(CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(DefaultTimeoutSeconds));

        (int ExitCode, string StdOut, string StdErr) r;
        try
        {
            r = _processRunner is not null
                ? await _processRunner("-p \"ping\" --output-format json --bare", cts.Token)
                : await RunAsync(_claudeExe, "-p \"ping\" --output-format json --bare", cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ClaudeVerdict(
                IsAvailable: false,
                Reason: "timeout",
                Remediation: "The `claude -p` probe timed out after 30s. Run `claude -p \"hi\"` manually to diagnose.");
        }
        catch (Exception ex) when (ex is FileNotFoundException or System.ComponentModel.Win32Exception)
        {
            _logger?.LogDebug(ex, "claude-verify: claude executable not found on PATH");
            return new ClaudeVerdict(
                IsAvailable: false,
                Reason: "not-installed",
                Remediation: "`claude` CLI not found on PATH. Install from https://claude.ai/install.ps1 (Windows) or https://claude.ai/install.sh (macOS/Linux).");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "claude-verify: probe threw");
            return new ClaudeVerdict(
                IsAvailable: false,
                Reason: "error",
                Remediation: $"Probe failed: {ex.Message}");
        }

        if (r.ExitCode != 0)
        {
            var hint = LooksLikeAuthFailure(r.StdErr, r.StdOut)
                ? "Not authenticated. Run `claude` interactively once to complete the /login flow."
                : $"`claude -p` exited {r.ExitCode}. stderr: {Truncate(r.StdErr, 400)}";
            return new ClaudeVerdict(
                IsAvailable: false,
                Reason: LooksLikeAuthFailure(r.StdErr, r.StdOut) ? "not-authenticated" : "error",
                Remediation: hint);
        }

        // Best-effort JSON parse — tolerate missing fields; presence of a non-empty
        // body is enough to confirm the CLI ran end-to-end with credentials.
        try
        {
            using var doc = JsonDocument.Parse(r.StdOut);
            var hasResult = doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("result", out var resultProp)
                && resultProp.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(resultProp.GetString());

            if (hasResult)
                return new ClaudeVerdict(IsAvailable: true, Reason: "ok", Remediation: string.Empty);
        }
        catch (JsonException)
        {
            // fall through
        }

        return new ClaudeVerdict(
            IsAvailable: false,
            Reason: "invalid-response",
            Remediation: "`claude -p` exited 0 but did not return a parseable JSON result. Try `claude -p \"hi\" --output-format json` manually.");
    }

    private static bool LooksLikeAuthFailure(string stderr, string stdout)
    {
        var combined = (stderr + " " + stdout).ToLowerInvariant();
        return combined.Contains("not logged in")
            || combined.Contains("/login")
            || combined.Contains("unauthenticated")
            || combined.Contains("authentication");
    }

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty
        : value.Length <= max ? value
        : value[..max] + "...";

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string fileName, string arguments, CancellationToken cancellationToken)
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

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
