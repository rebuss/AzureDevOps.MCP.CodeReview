using REBUSS.Pure.AzureDevOps.Configuration;
using REBUSS.Pure.Cli.AgentSetup;
using REBUSS.Pure.Cli.Deployment;
using REBUSS.Pure.Cli.Mcp;
using REBUSS.Pure.GitHub.Configuration;
using REBUSS.Pure.ProviderDetection;
using REBUSS.Pure.Properties;
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
    private readonly TextWriter _output;
    private readonly TextReader _input;
    private readonly InitCommandOptions _options;
    private readonly Func<string, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>>? _processRunner;
    private readonly ILocalConfigStore? _localConfigStore;
    private readonly IGitHubConfigStore? _gitHubConfigStore;
    private readonly Func<List<McpConfigTarget>>? _globalConfigTargetsResolver;

    public string Name => "init";

    public InitCommand(TextWriter output, string workingDirectory, string executablePath, string? pat = null, bool isGlobal = false, string? ide = null, string? agent = null)
        : this(
            output,
            Console.In,
            new InitCommandOptions(workingDirectory, executablePath, pat, isGlobal, ide, agent, DetectedProvider: null),
            processRunner: null,
            localConfigStore: null,
            gitHubConfigStore: null)
    {
    }

    public InitCommand(TextWriter output, TextReader input, string workingDirectory, string executablePath, string? pat = null, bool isGlobal = false, string? ide = null, string? agent = null, string? detectedProvider = null)
        : this(
            output,
            input,
            new InitCommandOptions(workingDirectory, executablePath, pat, isGlobal, ide, agent, detectedProvider),
            processRunner: null,
            localConfigStore: null,
            gitHubConfigStore: null)
    {
    }

    /// <summary>
    /// Constructor that accepts an optional input reader, detected provider, and process runner for testability.
    /// Builds an <see cref="InitCommandOptions"/> from the positional arguments and forwards to the canonical constructor.
    /// </summary>
    internal InitCommand(
        TextWriter output,
        TextReader input,
        string workingDirectory,
        string executablePath,
        string? pat,
        bool isGlobal,
        string? ide,
        string? agent,
        string? detectedProvider,
        Func<string, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>>? processRunner,
        ILocalConfigStore? localConfigStore = null,
        IGitHubConfigStore? gitHubConfigStore = null,
        Func<List<McpConfigTarget>>? globalConfigTargetsResolver = null)
        : this(
            output,
            input,
            new InitCommandOptions(workingDirectory, executablePath, pat, isGlobal, ide, agent, detectedProvider),
            processRunner,
            localConfigStore,
            gitHubConfigStore,
            globalConfigTargetsResolver)
    {
    }

    /// <summary>
    /// Canonical constructor — every public/internal shim above forwards here. Stores
    /// the user inputs as a single immutable <see cref="InitCommandOptions"/> record
    /// alongside the collaborator/test seams (output/input streams, process runner,
    /// config stores, global-target resolver).
    /// </summary>
    internal InitCommand(
        TextWriter output,
        TextReader input,
        InitCommandOptions options,
        Func<string, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>>? processRunner,
        ILocalConfigStore? localConfigStore = null,
        IGitHubConfigStore? gitHubConfigStore = null,
        Func<List<McpConfigTarget>>? globalConfigTargetsResolver = null)
    {
        _output = output;
        _input = input;
        _options = options;
        _processRunner = processRunner;
        _localConfigStore = localConfigStore;
        _gitHubConfigStore = gitHubConfigStore;
        _globalConfigTargetsResolver = globalConfigTargetsResolver;
    }

    public async Task<int> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var gitRoot = FindGitRepositoryRoot(_options.WorkingDirectory);
        if (gitRoot is null)
        {
            await _output.WriteLineAsync(Resources.ErrorNotInsideGitRepository);
            return 1;
        }

        // Resolve which AI agent to wire up. Explicit --agent flag wins; otherwise
        // prompt the user interactively. Default (empty input) is GitHub Copilot
        // to preserve prior behaviour for users upgrading without re-reading docs.
        var effectiveAgent = _options.Agent ?? await PromptForAgentAsync();

        // Create MCP config files and copy prompts FIRST — before any potentially
        // interactive or long-running Azure CLI steps. This ensures files are written
        // even if the user cancels during az install or az login.
        var targets = _options.IsGlobal
            ? (_globalConfigTargetsResolver?.Invoke() ?? McpConfigTargetResolver.ResolveGlobal(effectiveAgent))
            : McpConfigTargetResolver.Resolve(gitRoot, _options.Ide, effectiveAgent);

        await new McpConfigWriter(_output).WriteAllAsync(targets, _options.ExecutablePath, gitRoot, _options.Pat, effectiveAgent, cancellationToken);

        await new PromptDeployer(_output).DeployAsync(gitRoot, cancellationToken);
        await new ClaudeSkillDeployer(_output).DeployAsync(gitRoot, cancellationToken);
        await new LegacyClaudeCommandBackup(_output).RunAsync(gitRoot, cancellationToken);

        // Clear provider caches so the next server start detects fresh config from the new repo
        _localConfigStore?.Clear();
        _gitHubConfigStore?.Clear();

        string? ghCliPathOverride = null;
        if (string.IsNullOrWhiteSpace(_options.Pat))
        {
            var authFlow = CreateAuthFlow();
            await authFlow.RunAsync(cancellationToken);
            if (authFlow is GitHubCliAuthFlow ghFlow)
                ghCliPathOverride = ghFlow.GhCliPathOverride;
        }

        // Agent setup is intentionally non-fatal: any failure or decline is soft, and
        // the init exit code is not affected (FR-011).
        var setupStep = new AgentSetupStepFactory(_output, _input, _processRunner)
            .Create(effectiveAgent, ghCliPathOverride);
        await setupStep.RunAsync(cancellationToken);

        await _output.WriteLineAsync();
        await _output.WriteLineAsync(Resources.MsgMcpServerRepoHint);
        await _output.WriteLineAsync(Resources.MsgRestartIdeHint);

        return 0;
    }

    /// <summary>
    /// Creates the appropriate CLI authentication flow based on the detected provider.
    /// GitHub repos use <c>gh auth login</c>; Azure DevOps repos use <c>az login</c>.
    /// </summary>
    private ICliAuthFlow CreateAuthFlow()
    {
        var provider = _options.DetectedProvider ?? ProviderDetector.DetectFromGitRemote(_options.WorkingDirectory);

        if (string.Equals(provider, GitHubNames.Provider, StringComparison.OrdinalIgnoreCase))
            return new GitHubCliAuthFlow(_output, _input, _processRunner);

        return new AzureDevOpsCliAuthFlow(_output, _input, _processRunner);
    }

    /// <summary>
    /// Prompts the user to pick the AI agent to wire up. Returns
    /// <see cref="CliArgumentParser.AgentCopilot"/> on empty input (default)
    /// or <see cref="CliArgumentParser.AgentClaude"/> on <c>2</c>/<c>claude</c>.
    /// Never throws — on any I/O failure returns the safe default.
    /// </summary>
    internal async Task<string> PromptForAgentAsync()
    {
        await _output.WriteLineAsync();
        await _output.WriteLineAsync(Resources.MsgChooseAgentPrompt);
        await _output.WriteAsync(Resources.MsgChooseAgentPromptInline);

        string? answer;
        try { answer = await _input.ReadLineAsync(); }
        catch { return CliArgumentParser.AgentCopilot; }

        var normalized = (answer ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "2" or "claude" or "claude-code" => CliArgumentParser.AgentClaude,
            _ => CliArgumentParser.AgentCopilot
        };
    }

    private static string? FindGitRepositoryRoot(string startDirectory)
    {
        var dir = new DirectoryInfo(startDirectory);

        while (dir is not null)
        {
            var gitPath = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
                return dir.FullName;

            dir = dir.Parent;
        }

        return null;
    }
}

