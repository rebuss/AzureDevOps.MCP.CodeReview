namespace REBUSS.Pure.Cli.AgentSetup;

/// <summary>
/// Builds the <see cref="IAgentSetupStep"/> implementation matching the user's
/// <c>--agent</c> selection: <see cref="ClaudeAgentSetupStep"/> for
/// <see cref="CliArgumentParser.AgentClaude"/>; <see cref="CopilotAgentSetupStep"/>
/// otherwise (covers <see cref="CliArgumentParser.AgentCopilot"/>, unknown values,
/// and <c>null</c> — copilot is the safe default).
/// </summary>
internal sealed class AgentSetupStepFactory
{
    private readonly TextWriter _output;
    private readonly TextReader _input;
    private readonly Func<string, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>>? _processRunner;

    public AgentSetupStepFactory(
        TextWriter output,
        TextReader input,
        Func<string, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>>? processRunner)
    {
        _output = output;
        _input = input;
        _processRunner = processRunner;
    }

    public IAgentSetupStep Create(string? agent, string? ghCliPathOverride)
    {
        if (string.Equals(agent, CliArgumentParser.AgentClaude, StringComparison.OrdinalIgnoreCase))
            return new ClaudeAgentSetupStep(_output, _input, _processRunner);

        return new CopilotAgentSetupStep(_output, _input, _processRunner, ghCliPathOverride);
    }
}
