using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core.Services.AgentInvocation;

namespace REBUSS.Pure.Services.AgentInvocation;

/// <summary>
/// <see cref="IAgentInvoker"/> implementation that shells out to
/// <c>claude -p "&lt;prompt&gt;" --output-format json --bare</c> and returns the
/// assistant's final response text. Uses <c>--bare</c> so hooks, skills, and
/// MCP servers in <c>~/.claude</c> are not loaded — the subprocess is purely
/// a one-shot inference channel.
/// </summary>
public sealed class ClaudeCliAgentInvoker : IAgentInvoker
{
    private readonly ILogger<ClaudeCliAgentInvoker>? _logger;
    private readonly string _claudeExe;

    // Process-level timeout — a review page can be large and Claude may think for
    // several seconds, so give it room. The caller's CancellationToken can trim sooner.
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    public ClaudeCliAgentInvoker(
        ILogger<ClaudeCliAgentInvoker>? logger = null,
        string? claudeCliPathOverride = null)
    {
        _logger = logger;
        _claudeExe = string.IsNullOrWhiteSpace(claudeCliPathOverride) ? "claude" : claudeCliPathOverride;
    }

    public async Task<string> InvokeAsync(string prompt, string? model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(DefaultTimeout);

        // Build argument list — pass prompt via stdin to avoid OS command-line length
        // limits (Windows caps at ~32k; a full PR-page prompt easily exceeds that).
        var args = new List<string> { "-p", "--output-format", "json", "--bare" };
        if (!string.IsNullOrWhiteSpace(model))
        {
            args.Add("--model");
            args.Add(model);
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
            stdout = await process.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            stderr = await process.StandardError.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw;
        }

        if (process.ExitCode != 0)
        {
            _logger?.LogWarning("claude-invoker: claude -p exited {Exit}. stderr: {Stderr}",
                process.ExitCode, Truncate(stderr, 500));
            throw new InvalidOperationException(
                $"claude -p exited {process.ExitCode}. stderr: {Truncate(stderr, 500)}");
        }

        return ExtractResultFromJson(stdout);
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
