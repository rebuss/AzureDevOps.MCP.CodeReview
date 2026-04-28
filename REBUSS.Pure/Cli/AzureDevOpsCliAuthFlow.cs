using REBUSS.Pure.AzureDevOps.Configuration;

namespace REBUSS.Pure.Cli;

/// <summary>
/// Azure DevOps authentication flow for <c>rebuss-pure init</c>.
/// Checks for an existing Azure CLI session, runs <c>az login</c> if needed,
/// and caches the acquired token. Offers to install Azure CLI if not found.
/// </summary>
internal sealed class AzureDevOpsCliAuthFlow : ICliAuthFlow
{
    private readonly TextWriter _output;
    private readonly TextReader _input;
    private readonly Func<string, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>>? _processRunner;
    private string? _azCliPathOverride;

    public AzureDevOpsCliAuthFlow(
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
        // Check if Azure CLI is available
        if (!await IsAzCliInstalledAsync(cancellationToken))
        {
            var installed = await PromptAndInstallAzCliAsync(cancellationToken);
            if (!installed)
            {
                await WriteAuthFailureBannerAsync();
                return;
            }
        }

        // Check if a valid token is already cached
        var existingToken = await RunAzCliCommandAsync(
            $"account get-access-token --resource {AzureCliTokenProvider.AzureDevOpsResourceId} --output json",
            cancellationToken);

        if (existingToken.ExitCode == 0)
        {
            var parsed = AzureCliTokenProvider.ParseTokenResponse(existingToken.StdOut);
            if (parsed is not null && parsed.ExpiresOn > DateTime.UtcNow.AddMinutes(5))
            {
                CacheAzureCliToken(parsed);
                await _output.WriteLineAsync("Azure CLI: Using existing login session.");
                await _output.WriteLineAsync();
                return;
            }
        }

        // No valid token — attempt az login (interactive — inherits console)
        await _output.WriteLineAsync("No PAT provided. Attempting Azure CLI login...");
        await _output.WriteLineAsync("A browser window will open for authentication.");
        await _output.WriteLineAsync();

        var loginExitCode = await RunAzLoginInteractiveAsync(cancellationToken);
        if (loginExitCode != 0)
        {
            await WriteAuthFailureBannerAsync();
            return;
        }

        await _output.WriteLineAsync("Azure CLI login successful.");

        // Acquire and cache token
        var tokenResult = await RunAzCliCommandAsync(
            $"account get-access-token --resource {AzureCliTokenProvider.AzureDevOpsResourceId} --output json",
            cancellationToken);

        if (tokenResult.ExitCode == 0)
        {
            var token = AzureCliTokenProvider.ParseTokenResponse(tokenResult.StdOut);
            if (token is not null)
            {
                CacheAzureCliToken(token);
                await _output.WriteLineAsync("Azure DevOps token acquired and cached.");
                await _output.WriteLineAsync();
                return;
            }
        }

        await _output.WriteLineAsync("Warning: Login succeeded but token acquisition failed.");
        await _output.WriteLineAsync("The server will retry token acquisition at runtime.");
        await _output.WriteLineAsync();
    }

    private async Task<bool> IsAzCliInstalledAsync(CancellationToken cancellationToken)
    {
        var result = await RunAzCliCommandAsync("--version", cancellationToken);
        return result.ExitCode == 0;
    }

    private async Task<bool> PromptAndInstallAzCliAsync(CancellationToken cancellationToken)
    {
        await _output.WriteLineAsync("Azure CLI is not installed.");
        await _output.WriteLineAsync();
        await _output.WriteAsync("Would you like to install Azure CLI now? [y/N]: ");

        var response = _input.ReadLine();
        if (!string.Equals(response?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
        {
            await _output.WriteLineAsync();
            return false;
        }

        await _output.WriteLineAsync();
        await _output.WriteLineAsync("Installing Azure CLI...");
        await _output.WriteLineAsync();

        var installExitCode = await RunAzCliInstallAsync(cancellationToken);
        if (installExitCode != 0)
        {
            await _output.WriteLineAsync("Azure CLI installation failed.");
            await _output.WriteLineAsync("You can install it manually: https://aka.ms/install-azure-cli");
            await _output.WriteLineAsync();
            return false;
        }

        await _output.WriteLineAsync("Azure CLI installed successfully.");
        await _output.WriteLineAsync();

        // Verify installation
        if (!await IsAzCliInstalledAsync(cancellationToken))
        {
            if (_processRunner is null)
            {
                var foundPath = AzureCliProcessHelper.TryFindAzCliOnWindows();
                if (foundPath is not null)
                {
                    _azCliPathOverride = foundPath;
                    await _output.WriteLineAsync($"Azure CLI found at: {foundPath}");
                    return true;
                }
            }

            await _output.WriteLineAsync("Azure CLI was installed but could not be found.");
            await _output.WriteLineAsync("You may need to restart your terminal and run 'rebuss-pure init' again.");
            await _output.WriteLineAsync();
            return false;
        }

        return true;
    }

    private async Task<int> RunAzCliInstallAsync(CancellationToken cancellationToken)
    {
        if (_processRunner is not null)
        {
            var result = await _processRunner("install-az-cli", cancellationToken);
            return result.ExitCode;
        }

        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows))
        {
            return await CliProcessRunner.Shared.RunInteractiveAsync(
                "winget",
                "install -e --id Microsoft.AzureCLI --accept-source-agreements --accept-package-agreements",
                cancellationToken);
        }

        return await CliProcessRunner.Shared.RunInteractiveAsync(
            "bash", "-c \"curl -sL https://aka.ms/InstallAzureCLIDeb | sudo bash\"", cancellationToken);
    }

    private async Task WriteAuthFailureBannerAsync()
    {
        var appSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.Local.json");

        await _output.WriteLineAsync();
        await _output.WriteLineAsync("========================================");
        await _output.WriteLineAsync("  AUTHENTICATION NOT CONFIGURED");
        await _output.WriteLineAsync("========================================");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("Azure CLI login failed, was cancelled, or Azure CLI is not installed.");
        await _output.WriteLineAsync("PR review tools will NOT work until you authenticate.");
        await _output.WriteLineAsync("(Local self-review tools work without authentication.)");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("You have two options:");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("  OPTION 1 \u2014 Try again with Azure CLI (recommended):");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("    Install Azure CLI: https://aka.ms/install-azure-cli");
        await _output.WriteLineAsync("    Then run:");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("      rebuss-pure init");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("    A browser window will open for login.");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("  OPTION 2 \u2014 Use a Personal Access Token (PAT):");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync($"    Create the file: {appSettingsPath}");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("    With the following content:");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("      {");
        await _output.WriteLineAsync("        \"AzureDevOps\": {");
        await _output.WriteLineAsync("          \"PersonalAccessToken\": \"<your-pat-here>\"");
        await _output.WriteLineAsync("        }");
        await _output.WriteLineAsync("      }");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("    To create a PAT:");
        await _output.WriteLineAsync("      1. Go to https://dev.azure.com/<your-org>/_usersSettings/tokens");
        await _output.WriteLineAsync("      2. Click '+ New Token', select scope: Code (Read)");
        await _output.WriteLineAsync("      3. Copy the token into the file above");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("    Or pass it directly:");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("      rebuss-pure init --pat <your-pat-here>");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("========================================");
        await _output.WriteLineAsync();
    }

    private static void CacheAzureCliToken(AzureCliToken token)
    {
        try
        {
            var store = new LocalConfigStore(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalConfigStore>.Instance);
            var config = store.Load() ?? new CachedConfig();
            config.AccessToken = token.AccessToken;
            config.TokenType = "Bearer";
            config.TokenExpiresOn = token.ExpiresOn;
            store.Save(config);
        }
        catch
        {
            // Caching failure is non-fatal during init
        }
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunAzCliCommandAsync(
        string arguments, CancellationToken cancellationToken)
    {
        if (_processRunner is not null)
            return await _processRunner(arguments, cancellationToken);

        var (fileName, args) = AzureCliProcessHelper.GetProcessStartArgs(arguments, _azCliPathOverride);
        return await CliProcessRunner.Shared.RunAsync(fileName, args, cancellationToken);
    }

    private async Task<int> RunAzLoginInteractiveAsync(CancellationToken cancellationToken)
    {
        if (_processRunner is not null)
        {
            var result = await _processRunner("login --allow-no-subscriptions", cancellationToken);
            return result.ExitCode;
        }

        var envOverrides = new Dictionary<string, string>
        {
            ["AZURE_CORE_LOGIN_EXPERIENCE_V2"] = "off"
        };

        var (fileName, args) = AzureCliProcessHelper.GetProcessStartArgs("login --allow-no-subscriptions", _azCliPathOverride);
        return await CliProcessRunner.Shared.RunInteractiveAsync(fileName, args, cancellationToken, envOverrides);
    }
}
