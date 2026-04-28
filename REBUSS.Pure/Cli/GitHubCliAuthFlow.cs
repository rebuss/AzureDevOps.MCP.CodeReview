using REBUSS.Pure.GitHub.Configuration;

namespace REBUSS.Pure.Cli;

/// <summary>
/// GitHub authentication flow for <c>rebuss-pure init</c>.
/// Checks for an existing GitHub CLI session, runs <c>gh auth login</c> if needed,
/// and caches the acquired token. Offers to install GitHub CLI if not found.
/// </summary>
internal sealed class GitHubCliAuthFlow : ICliAuthFlow
{
    private readonly TextWriter _output;
    private readonly TextReader _input;
    private readonly Func<string, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>>? _processRunner;
    private string? _ghCliPathOverride;

    /// <summary>
    /// The resolved <c>gh.exe</c> path discovered after a fresh install when the current
    /// process PATH has not yet refreshed. <c>null</c> when <c>gh</c> is on PATH or was
    /// not installed by this flow. Exposed so that <see cref="CopilotCliSetupStep"/> can
    /// reuse the same override and avoid re-probing in the same <c>init</c> session.
    /// </summary>
    internal string? GhCliPathOverride => _ghCliPathOverride;

    public GitHubCliAuthFlow(
        TextWriter output,
        TextReader input,
        Func<string, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>>? processRunner = null)
    {
        _output = output;
        _input = input;
        _processRunner = processRunner;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        // Check if GitHub CLI is available
        if (!await IsGhCliInstalledAsync(cancellationToken))
        {
            var installed = await PromptAndInstallGhCliAsync(cancellationToken);
            if (!installed)
            {
                await WriteAuthFailureBannerAsync();
                return;
            }
        }

        // Check if user is already authenticated: gh auth token
        var existingToken = await RunGhCliCommandAsync("auth token", cancellationToken);
        if (existingToken.ExitCode == 0)
        {
            var parsed = GitHubCliTokenProvider.ParseTokenResponse(existingToken.StdOut);
            if (parsed is not null)
            {
                CacheGitHubCliToken(parsed);
                await _output.WriteLineAsync("GitHub CLI: Using existing login session.");
                await _output.WriteLineAsync();
                return;
            }
        }

        // No valid token — attempt gh auth login (interactive — opens browser)
        await _output.WriteLineAsync("No PAT provided. Attempting GitHub CLI login...");
        await _output.WriteLineAsync("A browser window will open for authentication.");
        await _output.WriteLineAsync();

        var loginExitCode = await RunGhAuthLoginInteractiveAsync(cancellationToken);
        if (loginExitCode != 0)
        {
            await WriteAuthFailureBannerAsync();
            return;
        }

        await _output.WriteLineAsync("GitHub CLI login successful.");

        // Acquire and cache token
        var tokenResult = await RunGhCliCommandAsync("auth token", cancellationToken);
        if (tokenResult.ExitCode == 0)
        {
            var token = GitHubCliTokenProvider.ParseTokenResponse(tokenResult.StdOut);
            if (token is not null)
            {
                CacheGitHubCliToken(token);
                await _output.WriteLineAsync("GitHub token acquired and cached.");
                await _output.WriteLineAsync();
                return;
            }
        }

        await _output.WriteLineAsync("Warning: Login succeeded but token acquisition failed.");
        await _output.WriteLineAsync("The server will retry token acquisition at runtime.");
        await _output.WriteLineAsync();
    }

    private async Task<bool> IsGhCliInstalledAsync(CancellationToken cancellationToken)
    {
        var result = await RunGhCliCommandAsync("--version", cancellationToken);
        return result.ExitCode == 0;
    }

    private async Task<bool> PromptAndInstallGhCliAsync(CancellationToken cancellationToken)
    {
        await _output.WriteLineAsync("GitHub CLI is not installed.");
        await _output.WriteLineAsync();
        await _output.WriteAsync("Would you like to install GitHub CLI now? [y/N]: ");

        var response = _input.ReadLine();
        if (!string.Equals(response?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
        {
            await _output.WriteLineAsync();
            return false;
        }

        await _output.WriteLineAsync();
        await _output.WriteLineAsync("Installing GitHub CLI...");
        await _output.WriteLineAsync();

        var installExitCode = await RunGhCliInstallAsync(cancellationToken);
        if (installExitCode != 0)
        {
            await _output.WriteLineAsync("GitHub CLI installation failed.");
            await _output.WriteLineAsync("You can install it manually: https://cli.github.com/");
            await _output.WriteLineAsync();
            return false;
        }

        await _output.WriteLineAsync("GitHub CLI installed successfully.");
        await _output.WriteLineAsync();

        // Verify installation
        if (!await IsGhCliInstalledAsync(cancellationToken))
        {
            if (_processRunner is null)
            {
                var foundPath = GitHubCliProcessHelper.TryFindGhCliOnWindows();
                if (foundPath is not null)
                {
                    _ghCliPathOverride = foundPath;
                    await _output.WriteLineAsync($"GitHub CLI found at: {foundPath}");
                    return true;
                }
            }

            await _output.WriteLineAsync("GitHub CLI was installed but could not be found.");
            await _output.WriteLineAsync("You may need to restart your terminal and run 'rebuss-pure init' again.");
            await _output.WriteLineAsync();
            return false;
        }

        return true;
    }

    private async Task<int> RunGhCliInstallAsync(CancellationToken cancellationToken)
    {
        if (_processRunner is not null)
        {
            var result = await _processRunner("install-gh-cli", cancellationToken);
            return result.ExitCode;
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
            "bash", "-c \"curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg | sudo dd of=/usr/share/keyrings/githubcli-archive-keyring.gpg && echo deb [arch=$(dpkg --print-architecture) signed-by=/usr/share/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main | sudo tee /etc/apt/sources.list.d/github-cli.list > /dev/null && sudo apt update && sudo apt install gh -y\"", cancellationToken);
    }

    private async Task WriteAuthFailureBannerAsync()
    {
        var appSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.Local.json");

        await _output.WriteLineAsync();
        await _output.WriteLineAsync("========================================");
        await _output.WriteLineAsync("  AUTHENTICATION NOT CONFIGURED");
        await _output.WriteLineAsync("========================================");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("GitHub CLI login failed, was cancelled, or GitHub CLI is not installed.");
        await _output.WriteLineAsync("PR review tools will NOT work until you authenticate.");
        await _output.WriteLineAsync("(Local self-review tools work without authentication.)");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("You have two options:");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("  OPTION 1 \u2014 Try again with GitHub CLI (recommended):");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("    Install GitHub CLI: https://cli.github.com/");
        await _output.WriteLineAsync("    Then run:");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("      gh auth login");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("    After that, run:");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("      rebuss-pure init");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("  OPTION 2 \u2014 Use a Personal Access Token (PAT):");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync($"    Create the file: {appSettingsPath}");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("    With the following content:");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("      {");
        await _output.WriteLineAsync("        \"GitHub\": {");
        await _output.WriteLineAsync("          \"PersonalAccessToken\": \"<your-token>\"");
        await _output.WriteLineAsync("        }");
        await _output.WriteLineAsync("      }");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("    To create a token:");
        await _output.WriteLineAsync("      1. Go to https://github.com/settings/tokens");
        await _output.WriteLineAsync("      2. Click 'Generate new token (classic)', select scope: repo");
        await _output.WriteLineAsync("      3. Copy the token into the file above");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("    Or pass it directly:");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("      rebuss-pure init --pat <your-token>");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("========================================");
        await _output.WriteLineAsync();
    }

    private static void CacheGitHubCliToken(GitHubCliToken token)
    {
        try
        {
            var store = new GitHubConfigStore(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<GitHubConfigStore>.Instance);
            var config = store.Load() ?? new GitHubCachedConfig();
            config.AccessToken = token.AccessToken;
            config.TokenExpiresOn = token.ExpiresOn;
            store.Save(config);
        }
        catch
        {
            // Caching failure is non-fatal during init
        }
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunGhCliCommandAsync(
        string arguments, CancellationToken cancellationToken)
    {
        if (_processRunner is not null)
            return await _processRunner(arguments, cancellationToken);

        var (fileName, args) = GitHubCliProcessHelper.GetProcessStartArgs(arguments, _ghCliPathOverride);
        return await CliProcessRunner.Shared.RunAsync(fileName, args, cancellationToken);
    }

    private async Task<int> RunGhAuthLoginInteractiveAsync(CancellationToken cancellationToken)
    {
        // `-s copilot` adds the Copilot OAuth scope on top of gh's default scopes
        // (gist, read:org, repo, workflow) so the minted token is Copilot-entitled.
        // Without it, the runtime Copilot SDK verification fails with NotAuthenticated
        // even though PR-review endpoints work fine.
        const string authLoginArgs = "auth login --web -s copilot";

        if (_processRunner is not null)
        {
            var result = await _processRunner(authLoginArgs, cancellationToken);
            return result.ExitCode;
        }

        var (fileName, args) = GitHubCliProcessHelper.GetProcessStartArgs(authLoginArgs, _ghCliPathOverride);
        return await CliProcessRunner.Shared.RunInteractiveAsync(fileName, args, cancellationToken);
    }
}
