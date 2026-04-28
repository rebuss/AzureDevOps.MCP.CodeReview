namespace REBUSS.Pure.Cli.AgentSetup;

/// <summary>
/// Polymorphic seam for the post-auth agent-specific setup step in
/// <see cref="InitCommand.ExecuteAsync"/>. Two implementations: <see cref="CopilotAgentSetupStep"/>
/// and <see cref="ClaudeAgentSetupStep"/>; the right one is picked by
/// <see cref="AgentSetupStepFactory.Create"/> based on the user's <c>--agent</c> selection.
/// Implementations are soft-exit: any failure or decline does NOT affect init's exit
/// code (FR-011).
/// </summary>
internal interface IAgentSetupStep
{
    Task RunAsync(CancellationToken cancellationToken);
}
