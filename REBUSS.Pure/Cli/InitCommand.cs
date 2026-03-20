using System.Diagnostics;
using System.Reflection;
using REBUSS.Pure.AzureDevOpsIntegration.Configuration;

namespace REBUSS.Pure.Cli;

/// <summary>
/// Generates MCP server configuration file(s) in the current Git repository
/// and copies review prompt files to <c>.github/prompts/</c> so that MCP clients
/// (e.g. VS Code, Visual Studio, GitHub Copilot) can launch the server and use the prompts.
/// <para>
/// When no <c>--pat</c> is provided, the command runs <c>az login</c> so the user
/// authenticates via Azure CLI. The acquired token is cached locally and the MCP server
/// will use it automatically at runtime.
/// </para>
/// <para>
/// The target location is determined by IDE auto-detection:
/// VS Code ? <c>.vscode/mcp.json</c>;
/// Visual Studio ? <c>.vs/mcp.json</c>;
/// both written when both IDEs are detected.
/// Falls back to VS Code when no IDE markers are found.
/// </para>
/// </summary>
public class InitCommand : ICliCommand
{
    private const string VsCodeDir = ".vscode";
    private const string VisualStudioDir = ".vs";
    private const string McpConfigFileName = "mcp.json";
    private const string ResourcePrefix = "REBUSS.Pure.Cli.Prompts.";

    private static readonly string[] PromptFileNames =
    {
        "review-pr.prompt.md",
        "self-review.prompt.md"
    };

    private readonly TextWriter _output;
    private readonly TextReader _input;
    private readonly string _workingDirectory;
    private readonly string _executablePath;
    private readonly string? _pat;
    private readonly Func<string, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>>? _processRunner;

    public string Name => "init";

    public InitCommand(TextWriter output, string workingDirectory, string executablePath, string? pat = null)
        : this(output, Console.In, workingDirectory, executablePath, pat, processRunner: null)
    {
    }

    public InitCommand(TextWriter output, TextReader input, string workingDirectory, string executablePath, string? pat = null)
        : this(output, input, workingDirectory, executablePath, pat, processRunner: null)
    {
    }

    /// <summary>
    /// Constructor that accepts an optional input reader and process runner for testability.
    /// </summary>
    internal InitCommand(
        TextWriter output,
        TextReader input,
        string workingDirectory,
        string executablePath,
        string? pat,
        Func<string, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>>? processRunner)
    {
        _output = output;
        _input = input;
        _workingDirectory = workingDirectory;
        _executablePath = executablePath;
        _pat = pat;
        _processRunner = processRunner;
    }

    public async Task<int> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var gitRoot = FindGitRepositoryRoot(_workingDirectory);
        if (gitRoot is null)
        {
            await _output.WriteLineAsync("Error: Not inside a Git repository. Run this command from a Git repository root.");
            return 1;
        }

        // Create MCP config files and copy prompts FIRST — before any potentially
        // interactive or long-running Azure CLI steps. This ensures files are written
        // even if the user cancels during az install or az login.
        var targets = ResolveConfigTargets(gitRoot);

        var normalizedExePath = _executablePath.Replace("\\", "\\\\");
        var normalizedRepoPath = gitRoot.Replace("\\", "\\\\");

        foreach (var target in targets)
        {
            Directory.CreateDirectory(target.Directory);

            string newContent;
            if (File.Exists(target.ConfigPath))
            {
                var existing = await File.ReadAllTextAsync(target.ConfigPath, cancellationToken);
                newContent = MergeConfigContent(existing, _executablePath, gitRoot, _pat);
                await File.WriteAllTextAsync(target.ConfigPath, newContent, cancellationToken);
                await _output.WriteLineAsync($"Updated MCP configuration ({target.IdeName}): {target.ConfigPath}");
            }
            else
            {
                newContent = BuildConfigContent(normalizedExePath, normalizedRepoPath, _pat);
                await File.WriteAllTextAsync(target.ConfigPath, newContent, cancellationToken);
                await _output.WriteLineAsync($"Created MCP configuration ({target.IdeName}): {target.ConfigPath}");
            }
        }

        await CopyPromptFilesAsync(gitRoot, cancellationToken);

        // Authenticate via Azure CLI after configs and prompts are already on disk
        if (string.IsNullOrWhiteSpace(_pat))
        {
            await TryAzureCliLoginAsync(cancellationToken);
        }

        await _output.WriteLineAsync();
        await _output.WriteLineAsync("The MCP server will be launched with --repo pointing to your workspace.");
        await _output.WriteLineAsync("Restart your IDE or reload the MCP client to pick up the new configuration.");

        return 0;
    }

    /// <summary>
    /// Attempts Azure CLI authentication: checks for an existing token first,
    /// runs <c>az login</c> if needed, then acquires and caches a token.
    /// If Azure CLI is not installed, offers to install it interactively.
    /// </summary>
    private async Task TryAzureCliLoginAsync(CancellationToken cancellationToken)
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

    /// <summary>
    /// Checks whether Azure CLI is installed by running <c>az --version</c>.
    /// </summary>
    private async Task<bool> IsAzCliInstalledAsync(CancellationToken cancellationToken)
    {
        var result = await RunAzCliCommandAsync("--version", cancellationToken);
        return result.ExitCode == 0;
    }

    /// <summary>
    /// Prompts the user to install Azure CLI. If confirmed, runs the appropriate
    /// platform installer (<c>winget</c> on Windows, <c>curl | bash</c> on Linux/macOS).
    /// Returns <c>true</c> if installation succeeded.
    /// </summary>
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
            await _output.WriteLineAsync("Azure CLI was installed but could not be found.");
            await _output.WriteLineAsync("You may need to restart your terminal and run 'rebuss-pure init' again.");
            await _output.WriteLineAsync();
            return false;
        }

        return true;
    }

    /// <summary>
    /// Runs the platform-specific Azure CLI installer interactively.
    /// On Windows uses <c>winget install -e --id Microsoft.AzureCLI</c>.
    /// On Linux/macOS uses <c>curl -sL https://aka.ms/InstallAzureCLIDeb | sudo bash</c>.
    /// </summary>
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
            return await RunInteractiveProcessAsync(
                "winget", "install -e --id Microsoft.AzureCLI", cancellationToken);
        }

        return await RunInteractiveProcessAsync(
            "bash", "-c \"curl -sL https://aka.ms/InstallAzureCLIDeb | sudo bash\"", cancellationToken);
    }

    /// <summary>
    /// Writes a prominent, actionable banner when Azure CLI login fails or is not available,
    /// explaining how the user can authenticate for Azure DevOps PR reviews.
    /// </summary>
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
        await _output.WriteLineAsync("  OPTION 1 — Try again with Azure CLI (recommended):");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("    Install Azure CLI: https://aka.ms/install-azure-cli");
        await _output.WriteLineAsync("    Then run:");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("      rebuss-pure init");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("    A browser window will open for login.");
        await _output.WriteLineAsync();
        await _output.WriteLineAsync("  OPTION 2 — Use a Personal Access Token (PAT):");
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

        var (fileName, args) = AzureCliProcessHelper.GetProcessStartArgs(arguments);
        return await RunProcessAsync(fileName, args, cancellationToken);
    }

    /// <summary>
    /// Runs <c>az login</c> interactively — the process inherits the parent console
    /// so it can open a browser and display progress to the user.
    /// </summary>
    private async Task<int> RunAzLoginInteractiveAsync(CancellationToken cancellationToken)
    {
        if (_processRunner is not null)
        {
            var result = await _processRunner("login --allow-no-subscriptions", cancellationToken);
            return result.ExitCode;
        }

        var (fileName, args) = AzureCliProcessHelper.GetProcessStartArgs("login --allow-no-subscriptions");
        return await RunInteractiveProcessAsync(fileName, args, cancellationToken);
    }

    internal static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(
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
                return (-1, string.Empty, "Failed to start process");

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

    /// <summary>
    /// Runs a process interactively — inherits the parent's stdin/stdout/stderr
    /// so the child can open a browser, display prompts, and interact with the user.
    /// Returns only the exit code (no captured output).
    /// </summary>
    internal static async Task<int> RunInteractiveProcessAsync(
        string fileName, string arguments, CancellationToken cancellationToken)
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

    /// <summary>
    /// Detects which IDE(s) are in use and returns the list of config file targets to write.
    /// Selection is based on which IDE folders physically exist:
    /// only <c>.vscode</c> ? VS Code only; only <c>.vs</c> ? Visual Studio only;
    /// both or neither ? both targets.
    /// </summary>
    internal static List<McpConfigTarget> ResolveConfigTargets(string gitRoot)
    {
        var targets = new List<McpConfigTarget>();

        bool hasVsCode = DetectsVsCode(gitRoot);
        bool hasVisualStudio = DetectsVisualStudio(gitRoot);

        bool writeVsCode = hasVsCode || !hasVisualStudio;
        bool writeVisualStudio = hasVisualStudio || !hasVsCode;

        if (writeVsCode)
            targets.Add(new McpConfigTarget(
                "VS Code",
                Path.Combine(gitRoot, VsCodeDir),
                Path.Combine(gitRoot, VsCodeDir, McpConfigFileName)));

        if (writeVisualStudio)
            targets.Add(new McpConfigTarget(
                "Visual Studio",
                Path.Combine(gitRoot, VisualStudioDir),
                Path.Combine(gitRoot, VisualStudioDir, McpConfigFileName)));

        return targets;
    }

    internal static bool DetectsVsCode(string gitRoot) =>
        Directory.Exists(Path.Combine(gitRoot, VsCodeDir)) ||
        Directory.EnumerateFiles(gitRoot, "*.code-workspace", SearchOption.TopDirectoryOnly).Any();

    internal static bool DetectsVisualStudio(string gitRoot) =>
        Directory.Exists(Path.Combine(gitRoot, VisualStudioDir)) ||
        Directory.EnumerateFiles(gitRoot, "*.sln", SearchOption.TopDirectoryOnly).Any();

    internal static string BuildConfigContent(string normalizedExePath, string normalizedRepoPath, string? pat = null)
    {
        var patArgs = string.IsNullOrWhiteSpace(pat)
            ? string.Empty
            : $", \"--pat\", \"{pat}\"";

        return $$"""
            {
              "servers": {
                "REBUSS.Pure": {
                  "type": "stdio",
                  "command": "{{normalizedExePath}}",
                  "args": ["--repo", "{{normalizedRepoPath}}"{{patArgs}}]
                }
              }
            }
            """;
    }

    /// <summary>
    /// Merges the REBUSS.Pure server entry into an existing <c>mcp.json</c> file,
    /// preserving any other server entries already present.
    /// Accepts raw (unescaped) paths — JSON escaping is handled by <see cref="System.Text.Json.Utf8JsonWriter"/>.
    /// Falls back to <see cref="BuildConfigContent"/> when the existing content is not valid JSON.
    /// </summary>
    internal static string MergeConfigContent(
        string existingJson,
        string rawExePath,
        string rawRepoPath,
        string? pat = null)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(existingJson);
            var root = doc.RootElement;

            var options = new System.Text.Json.JsonWriterOptions { Indented = true };
            using var ms = new System.IO.MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(ms, options))
            {
                writer.WriteStartObject();

                // Copy all top-level properties except "servers" verbatim
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name != "servers")
                        prop.WriteTo(writer);
                }

                // Write merged "servers" block
                writer.WritePropertyName("servers");
                writer.WriteStartObject();

                // Copy existing servers except REBUSS.Pure
                if (root.TryGetProperty("servers", out var serversEl))
                {
                    foreach (var server in serversEl.EnumerateObject())
                    {
                        if (server.Name != "REBUSS.Pure")
                            server.WriteTo(writer);
                    }
                }

                // Write the REBUSS.Pure entry — Utf8JsonWriter handles JSON escaping of raw paths
                // If no PAT was supplied, carry over any existing PAT from the current config.
                var effectivePat = pat;
                if (string.IsNullOrWhiteSpace(effectivePat))
                    effectivePat = ExtractExistingPat(root);

                writer.WritePropertyName("REBUSS.Pure");
                writer.WriteStartObject();
                writer.WriteString("type", "stdio");
                writer.WriteString("command", rawExePath);
                writer.WritePropertyName("args");
                writer.WriteStartArray();
                writer.WriteStringValue("--repo");
                writer.WriteStringValue(rawRepoPath);
                if (!string.IsNullOrWhiteSpace(effectivePat))
                {
                    writer.WriteStringValue("--pat");
                    writer.WriteStringValue(effectivePat);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();

                writer.WriteEndObject(); // servers
                writer.WriteEndObject(); // root
            }

            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }
        catch (System.Text.Json.JsonException)
        {
            // Existing file is not valid JSON — replace it entirely
            var normalizedExePath = rawExePath.Replace("\\", "\\\\");
            var normalizedRepoPath = rawRepoPath.Replace("\\", "\\\\");
            return BuildConfigContent(normalizedExePath, normalizedRepoPath, pat);
        }
    }

    /// <summary>
    /// Extracts the <c>--pat</c> argument value from an existing REBUSS.Pure server entry,
    /// or returns <c>null</c> if no PAT is present.
    /// </summary>
    private static string? ExtractExistingPat(System.Text.Json.JsonElement root)
    {
        if (!root.TryGetProperty("servers", out var servers))
            return null;

        if (!servers.TryGetProperty("REBUSS.Pure", out var entry))
            return null;

        if (!entry.TryGetProperty("args", out var args))
            return null;

        var argList = args.EnumerateArray().Select(a => a.GetString()).ToList();
        var patIndex = argList.IndexOf("--pat");
        if (patIndex >= 0 && patIndex + 1 < argList.Count)
            return argList[patIndex + 1];

        return null;
    }

    private async Task CopyPromptFilesAsync(string gitRoot, CancellationToken cancellationToken)
    {
        var promptsTargetDir = Path.Combine(gitRoot, ".github", "prompts");
        Directory.CreateDirectory(promptsTargetDir);

        var assembly = Assembly.GetExecutingAssembly();
        var copiedCount = 0;

        foreach (var promptFileName in PromptFileNames)
        {
            var resourceName = FindResourceName(assembly, promptFileName);

            if (resourceName is null)
            {
                await _output.WriteLineAsync($"Warning: Embedded prompt resource not found: {promptFileName}");
                continue;
            }

            var targetPath = Path.Combine(promptsTargetDir, promptFileName);

            if (File.Exists(targetPath))
            {
                await _output.WriteLineAsync($"Prompt already exists, skipping: {targetPath}");
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync(cancellationToken);

            await File.WriteAllTextAsync(targetPath, content, cancellationToken);
            copiedCount++;
        }

        if (copiedCount > 0)
            await _output.WriteLineAsync($"Copied {copiedCount} prompt file(s) to {promptsTargetDir}");
    }

    /// <summary>
    /// Locates the embedded resource name for a given prompt file.
    /// The SDK may mangle hyphens to underscores depending on version,
    /// so we search by suffix with both variants.
    /// </summary>
    internal static string? FindResourceName(Assembly assembly, string promptFileName)
    {
        var resources = assembly.GetManifestResourceNames();

        var exactName = ResourcePrefix + promptFileName;
        if (Array.Exists(resources, r => r == exactName))
            return exactName;

        var mangledName = ResourcePrefix + promptFileName.Replace('-', '_');
        if (Array.Exists(resources, r => r == mangledName))
            return mangledName;

        return null;
    }

    private static string? FindGitRepositoryRoot(string startDirectory)
    {
        var dir = new DirectoryInfo(startDirectory);

        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;

            dir = dir.Parent;
        }

        return null;
    }
}

/// <summary>
/// Describes a single MCP configuration file target to be written by <see cref="InitCommand"/>.
/// </summary>
internal sealed record McpConfigTarget(string IdeName, string Directory, string ConfigPath);
