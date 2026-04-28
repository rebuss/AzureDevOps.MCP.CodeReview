using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core.Services.CopilotReview;
using REBUSS.Pure.GitHub.Configuration;
using REBUSS.Pure.Properties;
using REBUSS.Pure.Services.CopilotReview;

namespace REBUSS.Pure.Cli;

/// <summary>
/// Optional setup step that runs at the end of <c>rebuss-pure init</c> and ensures
/// <c>gh</c> CLI is installed and authenticated with the Copilot scope, then verifies
/// the session against the bundled Copilot CLI. Runs regardless of SCM provider or
/// whether a <c>--pat</c> was supplied. Declining or failure is treated as a soft exit:
/// an informational banner is written and control returns to <see cref="InitCommand"/> without
/// throwing — the init exit code is never affected by this step (FR-011).
/// <para>
/// The Copilot CLI binary itself ships inside the REBUSS.Pure nupkg (see
/// <c>Build/BundleCopilotCliAllRids.targets</c>), so this step no longer installs
/// <c>gh copilot</c> as an extension or a built-in download. Its only job is to make
/// sure <c>gh</c>'s credential store contains a Copilot-entitled token that the bundled
/// CLI can read in <c>UseLoggedInUser</c> mode.
/// </para>
/// <para>
/// State detection is performed fresh on every invocation (no persisted decline memory,
/// per Clarification Q1). When <c>gh</c> itself is missing, the first prompt is framed as the
/// Copilot setup entry point and declining there skips the entire chain (Clarification Q2).
/// </para>
/// </summary>
internal sealed class CopilotCliSetupStep
{
    private readonly TextWriter _output;
    private readonly TextReader _input;
    private readonly Func<string, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>>? _processRunner;
    private readonly ILogger<CopilotCliSetupStep>? _logger;
    private readonly ICopilotVerificationProbe? _verificationProbe;
    private string? _ghCliPathOverride;

    public CopilotCliSetupStep(
        TextWriter output,
        TextReader input,
        Func<string, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>>? processRunner = null,
        string? ghCliPathOverride = null,
        ILogger<CopilotCliSetupStep>? logger = null,
        ICopilotVerificationProbe? verificationProbe = null)
    {
        _output = output;
        _input = input;
        _processRunner = processRunner;
        _ghCliPathOverride = ghCliPathOverride;
        _logger = logger;
        _verificationProbe = verificationProbe;
    }

    /// <summary>
    /// Runs the Copilot CLI setup flow. Never throws: any unhandled exception is logged at
    /// <see cref="LogLevel.Warning"/> and swallowed so that <see cref="InitCommand"/> can
    /// always return a success exit code regardless of the Copilot outcome.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await RunInternalAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Copilot CLI setup step failed with an unhandled exception");
            try { await WriteDeclineBannerAsync(); } catch { /* final fallback — swallow */ }
        }
    }

    private async Task RunInternalAsync(CancellationToken cancellationToken)
    {
        await _output.WriteLineAsync();

        // Step 1 — is `gh` CLI installed?
        if (!await IsGhInstalledAsync(cancellationToken))
        {
            // Copilot-framed entry prompt (Clarification Q2).
            // Declining here skips the entire chain.
            await _output.WriteLineAsync(Resources.CopilotSetup_ExplainBenefit);
            await _output.WriteAsync(Resources.CopilotSetup_PromptInstallGhAndContinue);

            if (!IsYes(ReadLine()))
            {
                await _output.WriteLineAsync();
                await WriteDeclineBannerAsync();
                _logger?.LogInformation("copilot-setup: declined-gh");
                return;
            }

            await _output.WriteLineAsync();
            await _output.WriteLineAsync("Installing GitHub CLI...");
            var installExit = await RunGhCliInstallAsync(cancellationToken);

            // After install, probe again. If PATH has not refreshed on Windows, try the known locations.
            if (installExit != 0 || !await IsGhInstalledAsync(cancellationToken))
            {
                var found = GitHubCliProcessHelper.TryFindGhCliOnWindows();
                if (found is not null)
                {
                    _ghCliPathOverride = found;
                    await _output.WriteLineAsync($"GitHub CLI found at: {found}");
                }

                if (!await IsGhInstalledAsync(cancellationToken))
                {
                    await _output.WriteLineAsync("GitHub CLI installation failed or executable not found on PATH.");
                    await WriteDeclineBannerAsync();
                    _logger?.LogWarning("copilot-setup: failed: gh-install");
                    return;
                }
            }

            // Authenticate — user already consented at the entry prompt.
            if (!await IsGhAuthenticatedAsync(cancellationToken))
            {
                await _output.WriteLineAsync("A browser window will open to authenticate GitHub CLI.");
                var loginExit = await RunGhInteractiveAsync("auth login --web -s copilot", cancellationToken);
                if (loginExit != 0 || !await IsGhAuthenticatedAsync(cancellationToken))
                {
                    await WriteDeclineBannerAsync();
                    _logger?.LogWarning("copilot-setup: login-failed");
                    return;
                }
            }

            await VerifyCopilotSessionAsync(cancellationToken);
            return;
        }

        // Step 2 — `gh` is installed. Check authentication.
        if (!await IsGhAuthenticatedAsync(cancellationToken))
        {
            if (!await PromptAndRunAuthLoginAsync(cancellationToken))
                return;
        }

        // Step 3 — gh installed + authenticated. Verify the session against the bundled Copilot CLI.
        await VerifyCopilotSessionAsync(cancellationToken);
    }

    /// <summary>
    /// Feature 018 (FR-017, T031). Runs the same Copilot verification as the
    /// runtime server start: delegates to <see cref="ICopilotVerificationProbe.ProbeAsync"/>
    /// and either prints a single confirmation line on success or writes the
    /// remediation banner on failure. Never throws — matches the existing
    /// soft-exit policy at lines 48-57: any exception is logged at Warning and
    /// swallowed so the init exit code stays 0.
    /// </summary>
    private async Task VerifyCopilotSessionAsync(CancellationToken cancellationToken)
    {
        if (_verificationProbe is null)
        {
            // No probe configured (e.g. DI bootstrap failed in InitCommand).
            // FR-017 soft-exit: silently skip the verification step.
            _logger?.LogDebug("copilot-setup: verification probe not configured, skipping");
            return;
        }

        var verdict = await TryProbeAsync(cancellationToken).ConfigureAwait(false);
        if (verdict is null) return;

        // Recovery: when the gh CLI session exists but lacks the Copilot scope, the
        // SDK reports NotAuthenticated even though `gh auth status` is fine. Try
        // `gh auth refresh -s copilot` once to add the scope, then re-probe. Only
        // applies to the LoggedInUser source — override tokens cannot be refreshed
        // via gh.
        if (!verdict.IsAvailable
            && verdict.Reason == CopilotAuthReason.NotAuthenticated
            && verdict.TokenSource == CopilotTokenSource.LoggedInUser)
        {
            var refreshed = await TryRefreshCopilotScopeAsync(cancellationToken).ConfigureAwait(false);
            if (refreshed)
            {
                var retry = await TryProbeAsync(cancellationToken).ConfigureAwait(false);
                if (retry is not null) verdict = retry;
            }
        }

        if (verdict.IsAvailable)
        {
            await _output.WriteLineAsync(string.Format(
                Resources.CopilotSetup_VerificationOk,
                verdict.TokenSource.ToLogLabel(),
                verdict.Login ?? "(unknown)",
                verdict.ConfiguredModel ?? "(unknown)"));
            _logger?.LogInformation("copilot-setup: verification-ok");
            return;
        }

        // Graceful degradation: print the banner but do NOT fail the init step.
        await WriteCopilotAuthFailureBannerAsync(verdict);
        _logger?.LogInformation("copilot-setup: verification-failed: {Reason}", verdict.Reason);
    }

    private async Task<CopilotVerdict?> TryProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _verificationProbe!.ProbeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "copilot-setup: verification probe threw — skipping");
            return null;
        }
    }

    /// <summary>
    /// Adds the Copilot OAuth scope to the existing <c>gh</c> session via
    /// <c>gh auth refresh -h github.com -s copilot</c>. Runs interactively so
    /// the user can complete the browser consent. Returns <c>true</c> only when
    /// the refresh command exits successfully.
    /// </summary>
    private async Task<bool> TryRefreshCopilotScopeAsync(CancellationToken cancellationToken)
    {
        await _output.WriteLineAsync();
        await _output.WriteLineAsync(
            "Copilot verification failed — the existing GitHub CLI session is missing the 'copilot' scope.");
        await _output.WriteLineAsync(
            "A browser window will open to grant the Copilot scope to your existing session.");

        int exitCode;
        try
        {
            exitCode = await RunGhInteractiveAsync(
                "auth refresh -h github.com -s copilot", cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "copilot-setup: auth refresh threw — skipping recovery");
            return false;
        }

        if (exitCode != 0)
        {
            _logger?.LogWarning("copilot-setup: auth refresh exited {Exit}", exitCode);
            return false;
        }

        _logger?.LogInformation("copilot-setup: copilot scope granted via auth refresh");
        return true;
    }

    private async Task WriteCopilotAuthFailureBannerAsync(CopilotVerdict verdict)
    {
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("========================================");
        await _output.WriteLineAsync(Resources.BannerCopilotNotAuthenticatedTitle);
        await _output.WriteLineAsync("========================================");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync(string.Format(
            Resources.BannerCopilotNotAuthenticatedBody,
            verdict.ConfiguredModel ?? "(unknown)",
            verdict.Reason,
            verdict.TokenSource.ToLogLabel(),
            verdict.Remediation));
        await _output.WriteLineAsync("========================================");
        await _output.WriteLineAsync();
    }

    /// <summary>
    /// Prompts the user to authenticate GitHub CLI via browser, then runs `gh auth login --web`.
    /// Returns <c>true</c> if authentication succeeded; <c>false</c> if user declined or login failed.
    /// </summary>
    private async Task<bool> PromptAndRunAuthLoginAsync(CancellationToken cancellationToken)
    {
        await _output.WriteLineAsync("GitHub CLI needs authentication to enable Copilot-powered review.");
        await _output.WriteAsync("A browser window will open for GitHub login. Continue? [y/N]: ");

        if (!IsYes(ReadLine()))
        {
            await _output.WriteLineAsync();
            await WriteDeclineBannerAsync();
            _logger?.LogInformation("copilot-setup: declined-auth");
            return false;
        }

        await _output.WriteLineAsync();
        // `-s copilot` grants the Copilot OAuth scope alongside gh's default scopes,
        // so the resulting session is Copilot-entitled and the SDK verification at the
        // end of init can succeed without a second manual step.
        var loginExit = await RunGhInteractiveAsync("auth login --web -s copilot", cancellationToken);
        if (loginExit != 0 || !await IsGhAuthenticatedAsync(cancellationToken))
        {
            await WriteDeclineBannerAsync();
            _logger?.LogWarning("copilot-setup: login-failed");
            return false;
        }

        return true;
    }

    private async Task WriteDeclineBannerAsync()
    {
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("========================================");
        await _output.WriteLineAsync(Resources.CopilotSetup_DeclineBannerTitle);
        await _output.WriteLineAsync("========================================");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync(Resources.CopilotSetup_DeclineBannerBody);
        await _output.WriteLineAsync();
        await _output.WriteLineAsync(Resources.CopilotSetup_ManualInstallHint);
        await _output.WriteLineAsync("========================================");
        await _output.WriteLineAsync();
    }

    private async Task<bool> IsGhInstalledAsync(CancellationToken cancellationToken)
    {
        var r = await RunGhCapturedAsync("--version", cancellationToken);
        return r.ExitCode == 0;
    }

    private async Task<bool> IsGhAuthenticatedAsync(CancellationToken cancellationToken)
    {
        var r = await RunGhCapturedAsync("auth status", cancellationToken);
        return r.ExitCode == 0;
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunGhCapturedAsync(
        string arguments, CancellationToken cancellationToken)
    {
        if (_processRunner is not null)
            return await _processRunner(arguments, cancellationToken);

        var (fileName, args) = GitHubCliProcessHelper.GetProcessStartArgs(arguments, _ghCliPathOverride);
        return await CliProcessRunner.Shared.RunAsync(fileName, args, cancellationToken);
    }

    private async Task<int> RunGhInteractiveAsync(string arguments, CancellationToken cancellationToken)
    {
        if (_processRunner is not null)
        {
            var r = await _processRunner(arguments, cancellationToken);
            return r.ExitCode;
        }

        var (fileName, args) = GitHubCliProcessHelper.GetProcessStartArgs(arguments, _ghCliPathOverride);
        return await CliProcessRunner.Shared.RunInteractiveAsync(fileName, args, cancellationToken);
    }

    private async Task<int> RunGhCliInstallAsync(CancellationToken cancellationToken)
    {
        if (_processRunner is not null)
        {
            var r = await _processRunner("install-gh-cli", cancellationToken);
            return r.ExitCode;
        }

        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows))
        {
            return await CliProcessRunner.Shared.RunInteractiveAsync(
                "winget",
                "install -e --id GitHub.cli --accept-source-agreements --accept-package-agreements",
                cancellationToken);
        }

        return await CliProcessRunner.Shared.RunInteractiveAsync(
            "bash",
            "-c \"curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg | sudo dd of=/usr/share/keyrings/githubcli-archive-keyring.gpg && echo deb [arch=$(dpkg --print-architecture) signed-by=/usr/share/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main | sudo tee /etc/apt/sources.list.d/github-cli.list > /dev/null && sudo apt update && sudo apt install gh -y\"",
            cancellationToken);
    }

    private string? ReadLine()
    {
        try { return _input.ReadLine(); }
        catch { return null; }
    }

    private static bool IsYes(string? response) =>
        string.Equals(response?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
}
