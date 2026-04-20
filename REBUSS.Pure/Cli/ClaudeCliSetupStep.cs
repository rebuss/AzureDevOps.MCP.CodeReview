using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Properties;
using REBUSS.Pure.Services.ClaudeCode;

namespace REBUSS.Pure.Cli;

/// <summary>
/// Optional setup step that runs at the end of <c>rebuss-pure init</c> when the user
/// selected Claude Code as their AI agent. Ensures <c>claude</c> CLI is installed
/// and authenticated (via the built-in <c>/login</c> flow), then verifies the session
/// with a single <c>claude -p</c> probe. Declining or any failure is a soft exit —
/// the init exit code is unaffected.
/// <para>
/// Install chain — per user policy, avoids a full <c>npm</c> install when possible:
/// <list type="number">
///   <item>Windows: <c>winget install Anthropic.ClaudeCode</c> → <c>irm https://claude.ai/install.ps1 | iex</c> (native standalone).</item>
///   <item>macOS: <c>brew install --cask claude-code</c> → <c>curl -fsSL https://claude.ai/install.sh | bash</c>.</item>
///   <item>Linux: <c>curl -fsSL https://claude.ai/install.sh | bash</c>.</item>
///   <item>Fallback only if the user already has <c>npm</c> on PATH: <c>npm install -g @anthropic-ai/claude-code</c> (extra y/N prompt).</item>
/// </list>
/// </para>
/// </summary>
internal sealed class ClaudeCliSetupStep
{
    private readonly TextWriter _output;
    private readonly TextReader _input;
    private readonly Func<string, string, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>>? _processRunner;
    private readonly ILogger<ClaudeCliSetupStep>? _logger;
    private readonly IClaudeVerificationProbe? _verificationProbe;
    private readonly string? _claudeCliPathOverride;

    public ClaudeCliSetupStep(
        TextWriter output,
        TextReader input,
        Func<string, string, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>>? processRunner = null,
        string? claudeCliPathOverride = null,
        ILogger<ClaudeCliSetupStep>? logger = null,
        IClaudeVerificationProbe? verificationProbe = null)
    {
        _output = output;
        _input = input;
        _processRunner = processRunner;
        _claudeCliPathOverride = claudeCliPathOverride;
        _logger = logger;
        _verificationProbe = verificationProbe;
    }

    /// <summary>
    /// Runs the Claude Code setup flow. Never throws — any unhandled exception
    /// is logged at <see cref="LogLevel.Warning"/> and swallowed so that
    /// <see cref="InitCommand"/> always returns a success exit code regardless
    /// of the Claude outcome.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await RunInternalAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Claude Code setup step failed with an unhandled exception");
            try { await WriteDeclineBannerAsync(); } catch { /* final fallback — swallow */ }
        }
    }

    private async Task RunInternalAsync(CancellationToken cancellationToken)
    {
        await _output.WriteLineAsync();

        // Step 1 — is `claude` CLI installed?
        if (!await IsClaudeInstalledAsync(cancellationToken))
        {
            await _output.WriteLineAsync(Resources.ClaudeSetup_ExplainBenefit);
            await _output.WriteAsync(Resources.ClaudeSetup_PromptInstall);

            if (!IsYes(ReadLine()))
            {
                await _output.WriteLineAsync();
                await WriteDeclineBannerAsync();
                _logger?.LogInformation("claude-setup: declined-install");
                return;
            }

            var installed = await TryInstallClaudeAsync(cancellationToken);
            if (!installed || !await IsClaudeInstalledAsync(cancellationToken))
            {
                await WriteDeclineBannerAsync();
                _logger?.LogWarning("claude-setup: install-failed");
                return;
            }

            await _output.WriteLineAsync(Resources.ClaudeSetup_InstallOk);
        }

        // Step 2 — verify auth by running the probe. If it reports not-authenticated,
        // launch `claude` interactively so the built-in /login flow can open a browser.
        var verdict = await ProbeAsync(cancellationToken);
        if (verdict is null)
        {
            // Probe not configured — skip silently
            _logger?.LogInformation("claude-setup: no-probe-configured");
            return;
        }

        if (verdict.IsAvailable)
        {
            await _output.WriteLineAsync(Resources.ClaudeSetup_VerifiedOk);
            _logger?.LogInformation("claude-setup: verified");
            return;
        }

        if (verdict.Reason == "not-authenticated")
        {
            await _output.WriteLineAsync();
            await _output.WriteLineAsync(Resources.ClaudeSetup_AuthInstructions);
            await _output.WriteAsync(Resources.ClaudeSetup_PromptLaunchAuth);

            if (!IsYes(ReadLine()))
            {
                await WriteAuthFailureBannerAsync(verdict);
                _logger?.LogInformation("claude-setup: declined-auth");
                return;
            }

            await RunClaudeInteractiveAsync(string.Empty, cancellationToken);

            // Re-probe after the user returns from interactive auth.
            var second = await ProbeAsync(cancellationToken);
            if (second is { IsAvailable: true })
            {
                await _output.WriteLineAsync(Resources.ClaudeSetup_VerifiedOk);
                _logger?.LogInformation("claude-setup: verified-after-auth");
                return;
            }

            await WriteAuthFailureBannerAsync(second ?? verdict);
            return;
        }

        await WriteAuthFailureBannerAsync(verdict);
    }

    private async Task<bool> TryInstallClaudeAsync(CancellationToken cancellationToken)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return await TryInstallOnWindowsAsync(cancellationToken);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return await TryInstallOnMacAsync(cancellationToken);

        return await TryInstallOnLinuxAsync(cancellationToken);
    }

    private async Task<bool> TryInstallOnWindowsAsync(CancellationToken cancellationToken)
    {
        // Prefer winget (no Node.js required, bundles the native standalone binary).
        if (await IsCommandAvailableAsync("winget", "--version", cancellationToken))
        {
            await _output.WriteLineAsync(Resources.ClaudeSetup_InstallingViaWinget);
            var r = await RunInteractiveAsync("winget",
                "install -e --id Anthropic.ClaudeCode --accept-source-agreements --accept-package-agreements",
                cancellationToken);
            if (r == 0 && await IsClaudeInstalledAsync(cancellationToken)) return true;
        }

        // Fallback: Anthropic's official install.ps1 script (also no Node.js).
        await _output.WriteLineAsync(Resources.ClaudeSetup_InstallingViaNativeScript);
        var psExit = await RunInteractiveAsync("powershell",
            "-NoProfile -ExecutionPolicy Bypass -Command \"irm https://claude.ai/install.ps1 | iex\"",
            cancellationToken);
        if (psExit == 0 && await IsClaudeInstalledAsync(cancellationToken)) return true;

        return await TryNpmFallbackAsync(cancellationToken);
    }

    private async Task<bool> TryInstallOnMacAsync(CancellationToken cancellationToken)
    {
        if (await IsCommandAvailableAsync("brew", "--version", cancellationToken))
        {
            await _output.WriteLineAsync(Resources.ClaudeSetup_InstallingViaBrew);
            var r = await RunInteractiveAsync("brew", "install --cask claude-code", cancellationToken);
            if (r == 0 && await IsClaudeInstalledAsync(cancellationToken)) return true;
        }

        await _output.WriteLineAsync(Resources.ClaudeSetup_InstallingViaNativeScript);
        var curlExit = await RunInteractiveAsync("bash",
            "-c \"curl -fsSL https://claude.ai/install.sh | bash\"",
            cancellationToken);
        if (curlExit == 0 && await IsClaudeInstalledAsync(cancellationToken)) return true;

        return await TryNpmFallbackAsync(cancellationToken);
    }

    private async Task<bool> TryInstallOnLinuxAsync(CancellationToken cancellationToken)
    {
        await _output.WriteLineAsync(Resources.ClaudeSetup_InstallingViaNativeScript);
        var curlExit = await RunInteractiveAsync("bash",
            "-c \"curl -fsSL https://claude.ai/install.sh | bash\"",
            cancellationToken);
        if (curlExit == 0 && await IsClaudeInstalledAsync(cancellationToken)) return true;

        return await TryNpmFallbackAsync(cancellationToken);
    }

    /// <summary>
    /// Last-resort install path via <c>npm</c> — only offered when <c>npm</c> is
    /// already on PATH (user policy: do not push a Node.js install on users who
    /// do not already have it). Asks for a separate y/N confirmation because
    /// adding a global npm package is a meaningful change.
    /// </summary>
    private async Task<bool> TryNpmFallbackAsync(CancellationToken cancellationToken)
    {
        if (!await IsCommandAvailableAsync("npm", "--version", cancellationToken))
        {
            await _output.WriteLineAsync(Resources.ClaudeSetup_NoNpmManualHint);
            return false;
        }

        await _output.WriteAsync(Resources.ClaudeSetup_PromptNpmFallback);
        if (!IsYes(ReadLine()))
            return false;

        var r = await RunInteractiveAsync("npm", "install -g @anthropic-ai/claude-code", cancellationToken);
        return r == 0;
    }

    private async Task<ClaudeVerdict?> ProbeAsync(CancellationToken cancellationToken)
    {
        if (_verificationProbe is null) return null;
        try { return await _verificationProbe.ProbeAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "claude-setup: verification probe threw");
            return null;
        }
    }

    private async Task<bool> IsClaudeInstalledAsync(CancellationToken cancellationToken)
    {
        var r = await RunClaudeCapturedAsync("--version", cancellationToken);
        return r.ExitCode == 0;
    }

    private async Task<bool> IsCommandAvailableAsync(string command, string probeArgs, CancellationToken cancellationToken)
    {
        try
        {
            var r = _processRunner is not null
                ? await _processRunner(command, probeArgs, cancellationToken)
                : await InitCommand.RunProcessAsync(command, probeArgs, cancellationToken);
            return r.ExitCode == 0;
        }
        catch { return false; }
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunClaudeCapturedAsync(
        string arguments, CancellationToken cancellationToken)
    {
        var exe = _claudeCliPathOverride ?? "claude";
        if (_processRunner is not null)
            return await _processRunner(exe, arguments, cancellationToken);
        return await InitCommand.RunProcessAsync(exe, arguments, cancellationToken);
    }

    private async Task<int> RunClaudeInteractiveAsync(string arguments, CancellationToken cancellationToken)
    {
        var exe = _claudeCliPathOverride ?? "claude";
        if (_processRunner is not null)
        {
            var r = await _processRunner(exe, arguments, cancellationToken);
            return r.ExitCode;
        }
        return await InitCommand.RunInteractiveProcessAsync(exe, arguments, cancellationToken);
    }

    private async Task<int> RunInteractiveAsync(string exe, string arguments, CancellationToken cancellationToken)
    {
        if (_processRunner is not null)
        {
            var r = await _processRunner(exe, arguments, cancellationToken);
            return r.ExitCode;
        }
        return await InitCommand.RunInteractiveProcessAsync(exe, arguments, cancellationToken);
    }

    private async Task WriteDeclineBannerAsync()
    {
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("========================================");
        await _output.WriteLineAsync(Resources.ClaudeSetup_DeclineBannerTitle);
        await _output.WriteLineAsync("========================================");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync(Resources.ClaudeSetup_DeclineBannerBody);
        await _output.WriteLineAsync("========================================");
        await _output.WriteLineAsync();
    }

    private async Task WriteAuthFailureBannerAsync(ClaudeVerdict verdict)
    {
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("========================================");
        await _output.WriteLineAsync(Resources.ClaudeSetup_AuthFailureBannerTitle);
        await _output.WriteLineAsync("========================================");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync(string.Format(Resources.ClaudeSetup_AuthFailureBannerBody,
            verdict.Reason, verdict.Remediation));
        await _output.WriteLineAsync("========================================");
        await _output.WriteLineAsync();
    }

    private string? ReadLine()
    {
        try { return _input.ReadLine(); }
        catch { return null; }
    }

    private static bool IsYes(string? response) =>
        string.Equals(response?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
}
