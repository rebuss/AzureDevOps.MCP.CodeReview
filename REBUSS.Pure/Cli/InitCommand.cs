using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.AzureDevOps.Configuration;
using REBUSS.Pure.Core.Services.CopilotReview;
using REBUSS.Pure.GitHub.Configuration;
using REBUSS.Pure.Properties;
using REBUSS.Pure.Services.CopilotReview;
using AzureDevOpsNames = REBUSS.Pure.AzureDevOps.Names;
using GitHubNames = REBUSS.Pure.GitHub.Names;

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
/// The target location can be forced with <c>--ide vscode</c> or <c>--ide vs</c>.
/// When <c>--ide</c> is not specified, the target is determined by IDE auto-detection:
/// VS Code → <c>.vscode/mcp.json</c>;
/// Visual Studio → <c>.vs/mcp.json</c>;
/// VS Code + Visual Studio are written when both are detected or when no markers are found.
/// </para>
/// </summary>
public class InitCommand : ICliCommand
{
    private const string VsCodeDir = ".vscode";
    private const string VisualStudioDir = ".vs";
    private const string McpConfigFileName = "mcp.json";
    private const string VsGlobalMcpConfigFileName = ".mcp.json";
    private const string ResourcePrefix = AppConstants.ServerName + ".Cli.Prompts.";

    private static readonly string[] PromptFileNames =
    {
        "review-pr.prompt.md",
        "self-review.prompt.md"
    };

    private static readonly string[] LegacyPromptFileNames =
    {
        "review-pr.md",
        "self-review.md"
    };

    private readonly TextWriter _output;
    private readonly TextReader _input;
    private readonly string _workingDirectory;
    private readonly string _executablePath;
    private readonly string? _pat;
    private readonly bool _isGlobal;
    private readonly string? _ide;
    private readonly string? _detectedProvider;
    private readonly Func<string, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>>? _processRunner;
    private readonly ILocalConfigStore? _localConfigStore;
    private readonly IGitHubConfigStore? _gitHubConfigStore;
    private readonly Func<List<McpConfigTarget>>? _globalConfigTargetsResolver;

    public string Name => "init";

    public InitCommand(TextWriter output, string workingDirectory, string executablePath, string? pat = null, bool isGlobal = false, string? ide = null)
        : this(output, Console.In, workingDirectory, executablePath, pat, isGlobal, ide, detectedProvider: null, processRunner: null, localConfigStore: null, gitHubConfigStore: null)
    {
    }

    public InitCommand(TextWriter output, TextReader input, string workingDirectory, string executablePath, string? pat = null, bool isGlobal = false, string? ide = null, string? detectedProvider = null)
        : this(output, input, workingDirectory, executablePath, pat, isGlobal, ide, detectedProvider, processRunner: null, localConfigStore: null, gitHubConfigStore: null)
    {
    }

    /// <summary>
    /// Constructor that accepts an optional input reader, detected provider, and process runner for testability.
    /// </summary>
    internal InitCommand(
        TextWriter output,
        TextReader input,
        string workingDirectory,
        string executablePath,
        string? pat,
        bool isGlobal,
        string? ide,
        string? detectedProvider,
        Func<string, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>>? processRunner,
        ILocalConfigStore? localConfigStore = null,
        IGitHubConfigStore? gitHubConfigStore = null,
        Func<List<McpConfigTarget>>? globalConfigTargetsResolver = null)
    {
        _output = output;
        _input = input;
        _workingDirectory = workingDirectory;
        _executablePath = executablePath;
        _pat = pat;
        _isGlobal = isGlobal;
        _ide = ide;
        _detectedProvider = detectedProvider;
        _processRunner = processRunner;
        _localConfigStore = localConfigStore;
        _gitHubConfigStore = gitHubConfigStore;
        _globalConfigTargetsResolver = globalConfigTargetsResolver;
    }

    public async Task<int> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var gitRoot = FindGitRepositoryRoot(_workingDirectory);
        if (gitRoot is null)
        {
            await _output.WriteLineAsync(Resources.ErrorNotInsideGitRepository);
            return 1;
        }

        // Create MCP config files and copy prompts FIRST — before any potentially
        // interactive or long-running Azure CLI steps. This ensures files are written
        // even if the user cancels during az install or az login.
        var targets = _isGlobal
            ? (_globalConfigTargetsResolver?.Invoke() ?? ResolveGlobalConfigTargets())
            : ResolveConfigTargets(gitRoot, _ide);

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
                await _output.WriteLineAsync(string.Format(Resources.MsgUpdatedMcpConfiguration, target.IdeName, target.ConfigPath));
            }
            else
            {
                newContent = BuildConfigContent(normalizedExePath, normalizedRepoPath, _pat);
                await File.WriteAllTextAsync(target.ConfigPath, newContent, cancellationToken);
                await _output.WriteLineAsync(string.Format(Resources.MsgCreatedMcpConfiguration, target.IdeName, target.ConfigPath));
            }
        }

        await CopyPromptFilesAsync(gitRoot, cancellationToken);

        // Clear provider caches so the next server start detects fresh config from the new repo
        _localConfigStore?.Clear();
        _gitHubConfigStore?.Clear();

        // Authenticate via the appropriate CLI flow after configs and prompts are already on disk
        string? ghCliPathOverride = null;
        if (string.IsNullOrWhiteSpace(_pat))
        {
            var authFlow = CreateAuthFlow();
            await authFlow.RunAsync(cancellationToken);
            if (authFlow is GitHubCliAuthFlow ghFlow)
                ghCliPathOverride = ghFlow.GhCliPathOverride;
        }

        // GitHub Copilot CLI setup (feature 012) — runs regardless of SCM provider or --pat.
        // This step is intentionally non-fatal: any failure or decline is soft, and the init
        // exit code is not affected (FR-011).
        // Feature 018 T032: also build a narrow throwaway service provider that exposes
        // ICopilotVerificationProbe so the setup step can verify the session and print
        // the remediation banner (FR-017). Probe construction failures are soft-exited.
        ServiceProvider? copilotProbeServices = null;
        ICopilotVerificationProbe? verificationProbe = null;
        try
        {
            copilotProbeServices = BuildCopilotProbeServices();
            verificationProbe = copilotProbeServices.GetRequiredService<ICopilotVerificationProbe>();
        }
        catch (Exception ex)
        {
            await _output.WriteLineAsync(
                $"Warning: could not construct Copilot verification probe ({ex.Message}). Skipping verification step.");
            verificationProbe = null;
        }

        try
        {
            var copilotStep = new CopilotCliSetupStep(
                _output, _input, _processRunner, ghCliPathOverride,
                verificationProbe: verificationProbe);
            await copilotStep.RunAsync(cancellationToken);
        }
        catch
        {
            // Defense in depth — CopilotCliSetupStep is already catch-all internally.
        }
        finally
        {
            copilotProbeServices?.Dispose();
        }

        await _output.WriteLineAsync();
        await _output.WriteLineAsync(Resources.MsgMcpServerRepoHint);
        await _output.WriteLineAsync(Resources.MsgRestartIdeHint);

        return 0;
    }

    /// <summary>
    /// Feature 018 T032: builds a narrow, throwaway service provider that registers
    /// only the types needed by <see cref="ICopilotVerificationProbe"/> — this init
    /// flow runs outside of the MCP host DI graph. The provider is disposed as soon
    /// as <see cref="CopilotCliSetupStep.RunAsync"/> returns.
    /// </summary>
    private static ServiceProvider BuildCopilotProbeServices()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Local.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.Configure<CopilotReviewOptions>(
            configuration.GetSection(CopilotReviewOptions.SectionName));
        services.AddSingleton<ICopilotTokenResolver, CopilotTokenResolver>();
        services.AddSingleton<CopilotVerificationRunner>();
        services.AddSingleton<ICopilotVerificationProbe>(
            sp => sp.GetRequiredService<CopilotVerificationRunner>());

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Creates the appropriate CLI authentication flow based on the detected provider.
    /// GitHub repos use <c>gh auth login</c>; Azure DevOps repos use <c>az login</c>.
    /// </summary>
    private ICliAuthFlow CreateAuthFlow()
    {
        var provider = _detectedProvider ?? DetectProviderFromGitRemote(_workingDirectory);

        if (string.Equals(provider, GitHubNames.Provider, StringComparison.OrdinalIgnoreCase))
            return new GitHubCliAuthFlow(_output, _input, _processRunner);

        return new AzureDevOpsCliAuthFlow(_output, _input, _processRunner);
    }

    /// <summary>
    /// Auto-detects the SCM provider from the git remote URL of the working directory.
    /// Returns <c>"GitHub"</c> if the remote points to github.com, otherwise <c>"AzureDevOps"</c>.
    /// </summary>
    internal static string DetectProviderFromGitRemote(string workingDirectory)
    {
        try
        {
            var gitRoot = FindGitRepositoryRoot(workingDirectory);
            if (gitRoot is null) return AzureDevOpsNames.Provider;

            var psi = new ProcessStartInfo
            {
                FileName = Resources.GitExecutable,
                Arguments = Resources.GitRemoteGetUrlArgs,
                WorkingDirectory = gitRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null) return AzureDevOpsNames.Provider;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(TimeSpan.FromSeconds(5));

            if (process.ExitCode != 0) return AzureDevOpsNames.Provider;

            if (output.Contains(GitHubNames.Domain, StringComparison.OrdinalIgnoreCase))
                return GitHubNames.Provider;
        }
        catch
        {
            // Ignore detection errors — fall back to Azure DevOps
        }

        return AzureDevOpsNames.Provider;
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
                return (-1, string.Empty, Resources.ErrorFailedToStartProcess);

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
        string fileName, string arguments, CancellationToken cancellationToken,
        IDictionary<string, string>? environmentOverrides = null)
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

            if (environmentOverrides is not null)
            {
                foreach (var (key, value) in environmentOverrides)
                    psi.Environment[key] = value;
            }

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
    /// When <paramref name="ide"/> is provided (<c>"vscode"</c> or <c>"vs"</c>), only that
    /// IDE's target is returned — no auto-detection is performed.
    /// Otherwise, selection is based on which IDE folders physically exist:
    /// only <c>.vscode</c> → VS Code only; only <c>.vs</c> → Visual Studio only;
    /// both or neither → both targets.
    /// </summary>
    internal static List<McpConfigTarget> ResolveConfigTargets(string gitRoot, string? ide = null)
    {
        if (!string.IsNullOrWhiteSpace(ide))
        {
            if (string.Equals(ide, "vscode", StringComparison.OrdinalIgnoreCase))
                return
                [
                    new McpConfigTarget(
                        "VS Code",
                        Path.Combine(gitRoot, VsCodeDir),
                        Path.Combine(gitRoot, VsCodeDir, McpConfigFileName))
                ];

            if (string.Equals(ide, "vs", StringComparison.OrdinalIgnoreCase))
                return
                [
                    new McpConfigTarget(
                        "Visual Studio",
                        Path.Combine(gitRoot, VisualStudioDir),
                        Path.Combine(gitRoot, VisualStudioDir, McpConfigFileName))
                ];

            }

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

    /// <summary>
    /// Returns global (user-level) MCP configuration targets.
    /// Visual Studio reads <c>~/.mcp.json</c> directly from the user's home directory.
    /// VS Code reads <c>%APPDATA%/Code/User/mcp.json</c> on Windows
    /// (<c>~/.config/Code/User/mcp.json</c> on Linux).
    /// Writing to both ensures every workspace picks up the configuration.
    /// </summary>
    internal static List<McpConfigTarget> ResolveGlobalConfigTargets()
    {
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData  = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        return
        [
            new McpConfigTarget(
                "Visual Studio (global)",
                userHome,
                Path.Combine(userHome, VsGlobalMcpConfigFileName)),

            new McpConfigTarget(
                "VS Code (global)",
                Path.Combine(appData, "Code", "User"),
                Path.Combine(appData, "Code", "User", McpConfigFileName))
        ];
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

        var serversKey = "servers";

        return $$"""
            {
              "{{serversKey}}": {
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
        var serversKey = "servers";

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(existingJson);
            var root = doc.RootElement;

            var options = new System.Text.Json.JsonWriterOptions { Indented = true };
            using var ms = new System.IO.MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(ms, options))
            {
                writer.WriteStartObject();

                // Copy all top-level properties except the servers key verbatim
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name != serversKey)
                        prop.WriteTo(writer);
                }

                // Write merged servers block
                writer.WritePropertyName(serversKey);
                writer.WriteStartObject();

                // Copy existing servers except REBUSS.Pure
                if (root.TryGetProperty(serversKey, out var serversEl))
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
                    effectivePat = ExtractExistingPat(root, serversKey);

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
    private static string? ExtractExistingPat(System.Text.Json.JsonElement root, string serversKey = "servers")
    {
        if (!root.TryGetProperty(serversKey, out var servers))
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

        await DeleteLegacyPromptFilesAsync(promptsTargetDir);

        var assembly = Assembly.GetExecutingAssembly();
        var promptsWritten = 0;

        foreach (var promptFileName in PromptFileNames)
        {
            var resourceName = FindResourceName(assembly, promptFileName);

            if (resourceName is null)
            {
                await _output.WriteLineAsync(string.Format(Resources.WarnEmbeddedPromptResourceNotFound, promptFileName));
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync(cancellationToken);

            // Always write to .github/prompts/ (overwrite enables prompt updates)
            var promptPath = Path.Combine(promptsTargetDir, promptFileName);
            await File.WriteAllTextAsync(promptPath, content, cancellationToken);
            promptsWritten++;
        }

        if (promptsWritten > 0)
            await _output.WriteLineAsync(string.Format(Resources.MsgCopiedPrompts, promptsWritten, promptsTargetDir));
    }

    private async Task DeleteLegacyPromptFilesAsync(string promptsTargetDir)
    {
        foreach (var legacyFileName in LegacyPromptFileNames)
        {
            var legacyPath = Path.Combine(promptsTargetDir, legacyFileName);
            if (!File.Exists(legacyPath))
                continue;

            File.Delete(legacyPath);
            await _output.WriteLineAsync(string.Format(Resources.MsgDeletedLegacyPromptFile, legacyPath));
        }
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
