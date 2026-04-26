using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core.Services.AgentInvocation;

namespace REBUSS.Pure.Services.AgentInvocation;

/// <summary>
/// <see cref="IAgentInvoker"/> implementation that shells out to
/// <c>claude -p &lt;prompt&gt; --output-format json</c> and returns the assistant's
/// final response text.
/// <para>
/// Auth mode is selected from the environment at call time:
/// <list type="bullet">
///   <item>If <c>ANTHROPIC_API_KEY</c> is set, the call adds <c>--bare</c> so hooks,
///   skills, and MCP servers under <c>~/.claude</c> are not loaded — the subprocess
///   becomes a pure one-shot inference channel that bills the API console account.</item>
///   <item>Otherwise <c>--bare</c> is omitted so the CLI falls back to the user's
///   subscription credential — either a persistent OAuth login from
///   <c>claude /login</c> or a long-lived <c>CLAUDE_CODE_OAUTH_TOKEN</c>. (Bare mode
///   refuses to read those credentials, hence the conditional.)</item>
/// </list>
/// </para>
/// </summary>
public sealed class ClaudeCliAgentInvoker : IAgentInvoker
{
    internal const string ApiKeyEnvVar = "ANTHROPIC_API_KEY";

    private readonly ILogger<ClaudeCliAgentInvoker>? _logger;
    private readonly string _claudeExe;
    private readonly Func<string, string?> _envLookup;

    // Process-level timeout — a review page can be large and Claude may think for
    // several seconds, so give it room. The caller's CancellationToken can trim sooner.
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    public ClaudeCliAgentInvoker(
        ILogger<ClaudeCliAgentInvoker>? logger = null,
        string? claudeCliPathOverride = null,
        Func<string, string?>? environmentLookup = null)
    {
        _logger = logger;
        _claudeExe = string.IsNullOrWhiteSpace(claudeCliPathOverride) ? "claude" : claudeCliPathOverride;
        _envLookup = environmentLookup ?? Environment.GetEnvironmentVariable;
    }

    public async Task<string> InvokeAsync(string prompt, string? model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(DefaultTimeout);

        // Build argument list — pass prompt via stdin to avoid OS command-line length
        // limits (Windows caps at ~32k; a full PR-page prompt easily exceeds that).
        var args = new List<string> { "-p", "--output-format", "json" };
        if (ShouldUseBareMode(_envLookup))
            args.Add("--bare");
        if (!string.IsNullOrWhiteSpace(model))
        {
            args.Add("--model");
            args.Add(NormalizeModelForClaudeCli(model));
        }

        var psi = new ProcessStartInfo
        {
            FileName = _claudeExe,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{_claudeExe}'.");

        // Write prompt to stdin and close — Claude reads the prompt from stdin in -p mode.
        try
        {
            await process.StandardInput.WriteAsync(prompt.AsMemory(), cts.Token).ConfigureAwait(false);
        }
        finally
        {
            process.StandardInput.Close();
        }

        string stdout, stderr;
        try
        {
            // Drain stdout and stderr concurrently — sequential reads can deadlock
            // when the CLI fills the stderr pipe buffer while we're still consuming
            // stdout (the child blocks on the unread pipe and never closes stdout).
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            stdout = stdoutTask.Result;
            stderr = stderrTask.Result;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw;
        }

        if (process.ExitCode != 0)
        {
            // `claude -p --output-format json` reports auth/quota/etc. errors in the JSON
            // payload on stdout — stderr is usually empty. Including both in the message
            // is the difference between a silent failure and an actionable one.
            var detail = FormatFailureDetail(stdout, stderr);
            _logger?.LogWarning("claude-invoker: claude -p exited {Exit}. {Detail}",
                process.ExitCode, detail);
            throw new InvalidOperationException(
                $"claude -p exited {process.ExitCode}. {detail}");
        }

        return ExtractResultFromJson(stdout);
    }

    /// <summary>
    /// Returns true when <c>--bare</c> should be appended to the <c>claude -p</c>
    /// command. Bare mode requires <c>ANTHROPIC_API_KEY</c> (it ignores OAuth
    /// credentials), so we only enable it when that env var is present.
    /// </summary>
    internal static bool ShouldUseBareMode(Func<string, string?> envLookup) =>
        !string.IsNullOrWhiteSpace(envLookup(ApiKeyEnvVar));

    /// <summary>
    /// Translates the shared <c>CopilotReviewOptions.Model</c> value into the
    /// canonical Claude model id form. Copilot's catalogue uses dotted versions
    /// (e.g. <c>claude-sonnet-4.6</c>); the Claude CLI rejects those with HTTP 404
    /// and accepts only the hyphenated form (<c>claude-sonnet-4-6</c>). All current
    /// Anthropic model ids use hyphens, so dot→hyphen substitution is safe here
    /// and idempotent for already-correct ids.
    /// </summary>
    internal static string NormalizeModelForClaudeCli(string model) =>
        model.Replace('.', '-');

    /// <summary>
    /// Builds the operator-facing detail line for a non-zero exit. Includes both
    /// stdout and stderr (truncated) because claude-cli reports JSON errors on stdout
    /// while shell-level failures land on stderr — checking only one hides half the failures.
    /// </summary>
    internal static string FormatFailureDetail(string stdout, string stderr)
    {
        var stdoutTrim = Truncate(stdout, 500);
        var stderrTrim = Truncate(stderr, 500);
        if (stdoutTrim.Length == 0 && stderrTrim.Length == 0)
            return "no output captured.";
        if (stdoutTrim.Length == 0)
            return $"stderr: {stderrTrim}";
        if (stderrTrim.Length == 0)
            return $"stdout: {stdoutTrim}";
        return $"stdout: {stdoutTrim} | stderr: {stderrTrim}";
    }

    /// <summary>
    /// Parses the JSON payload produced by <c>claude -p --output-format json</c>
    /// and returns the <c>result</c> field. Falls back to raw stdout when the
    /// shape is unexpected — Claude's wrapper format has changed across versions
    /// and we prefer returning something over throwing.
    /// </summary>
    internal static string ExtractResultFromJson(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("result", out var resultProp)
                && resultProp.ValueKind == JsonValueKind.String)
            {
                return resultProp.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // Fall through to raw stdout
        }

        return stdout;
    }

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty
        : value.Length <= max ? value
        : value[..max] + "...";
}
