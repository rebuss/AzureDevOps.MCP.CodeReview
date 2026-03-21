using System.Diagnostics;
using System.Reflection;

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
        "review-pr.md",
        "self-review.md"
    };

    private readonly TextWriter _output;
    private readonly TextReader _input;
    private readonly string _workingDirectory;
    private readonly string _executablePath;
    private readonly string? _pat;
    private readonly string? _detectedProvider;
    private readonly Func<string, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>>? _processRunner;

    public string Name => "init";

    public InitCommand(TextWriter output, string workingDirectory, string executablePath, string? pat = null)
        : this(output, Console.In, workingDirectory, executablePath, pat, detectedProvider: null, processRunner: null)
    {
    }

    public InitCommand(TextWriter output, TextReader input, string workingDirectory, string executablePath, string? pat = null, string? detectedProvider = null)
        : this(output, input, workingDirectory, executablePath, pat, detectedProvider, processRunner: null)
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
        string? detectedProvider,
        Func<string, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>>? processRunner)
    {
        _output = output;
        _input = input;
        _workingDirectory = workingDirectory;
        _executablePath = executablePath;
        _pat = pat;
        _detectedProvider = detectedProvider;
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

        // Authenticate via the appropriate CLI flow after configs and prompts are already on disk
        if (string.IsNullOrWhiteSpace(_pat))
        {
            var authFlow = CreateAuthFlow();
            await authFlow.RunAsync(cancellationToken);
        }

        await _output.WriteLineAsync();
        await _output.WriteLineAsync("The MCP server will be launched with --repo pointing to your workspace.");
        await _output.WriteLineAsync("Restart your IDE or reload the MCP client to pick up the new configuration.");

        return 0;
    }

    /// <summary>
    /// Creates the appropriate CLI authentication flow based on the detected provider.
    /// GitHub repos use <c>gh auth login</c>; Azure DevOps repos use <c>az login</c>.
    /// </summary>
    private ICliAuthFlow CreateAuthFlow()
    {
        var provider = _detectedProvider ?? DetectProviderFromGitRemote(_workingDirectory);

        if (string.Equals(provider, "GitHub", StringComparison.OrdinalIgnoreCase))
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
            if (gitRoot is null) return "AzureDevOps";

            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "remote get-url origin",
                WorkingDirectory = gitRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null) return "AzureDevOps";

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(TimeSpan.FromSeconds(5));

            if (process.ExitCode != 0) return "AzureDevOps";

            if (output.Contains("github.com", StringComparison.OrdinalIgnoreCase))
                return "GitHub";
        }
        catch
        {
            // Ignore detection errors — fall back to Azure DevOps
        }

        return "AzureDevOps";
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
