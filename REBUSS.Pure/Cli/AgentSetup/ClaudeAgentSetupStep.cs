using REBUSS.Pure.Services.ClaudeCode;

namespace REBUSS.Pure.Cli.AgentSetup;

/// <summary>
/// Wires up the Claude Code CLI. The Claude probe does not need SDK-level DI; it
/// shells out to <c>claude -p</c> directly. Any failure is soft-exited so the init
/// exit code is unaffected. Includes a runner-arity adapter that converts the 2-arg
/// <c>processRunner</c> (full command string) into the 3-arg signature
/// (<c>exe</c>, <c>args</c>, <c>ct</c>) the Claude step uses for cross-tool install calls
/// — exe and args MUST be concatenated, since discarding exe would feed the injected
/// runner ambiguous fragments shared by <c>claude</c>/<c>winget</c>/<c>npm</c>.
/// </summary>
internal sealed class ClaudeAgentSetupStep : IAgentSetupStep
{
    private readonly TextWriter _output;
    private readonly TextReader _input;
    private readonly Func<string, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>>? _processRunner;

    public ClaudeAgentSetupStep(
        TextWriter output,
        TextReader input,
        Func<string, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>>? processRunner)
    {
        _output = output;
        _input = input;
        _processRunner = processRunner;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Convert the 2-arg _processRunner (takes the full command string) into the
            // 3-arg (exe, args, ct) signature the Claude step uses for cross-tool install
            // calls. exe and args MUST be concatenated — discarding exe would feed the
            // injected runner ambiguous fragments ("--version") shared by `claude`,
            // `winget`, `npm`, etc., breaking probe-result disambiguation.
            Func<string, string, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>>? runner = null;
            if (_processRunner is not null)
                runner = (exe, args, ct) => _processRunner($"{exe} {args}", ct);

            var probe = new ClaudeVerificationRunner(
                logger: null,
                processRunner: _processRunner);

            var claudeStep = new ClaudeCliSetupStep(
                _output, _input,
                processRunner: runner,
                verificationProbe: probe);
            await claudeStep.RunAsync(cancellationToken);
        }
        catch
        {
            // Defense in depth — ClaudeCliSetupStep is already catch-all internally.
        }
    }
}
