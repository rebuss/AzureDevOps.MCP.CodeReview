using REBUSS.Pure.Cli;
using REBUSS.Pure.Cli.AgentSetup;

namespace REBUSS.Pure.Tests.Cli.AgentSetup;

/// <summary>
/// Step 7 — verifies <see cref="AgentSetupStepFactory.Create"/> picks the right
/// implementation. Each agent step's run-time behaviour is covered by the existing
/// <c>CopilotCliSetupStepTests</c> / <c>ClaudeCliSetupStepTests</c> integration suites
/// — the unit test here is a thin dispatch contract.
/// </summary>
public class AgentSetupStepFactoryTests
{
    private static AgentSetupStepFactory NewFactory() =>
        new(new StringWriter(), new StringReader(""), processRunner: null);

    [Fact]
    public void Create_AgentClaude_ReturnsClaudeAgentSetupStep()
    {
        var step = NewFactory().Create(CliArgumentParser.AgentClaude, ghCliPathOverride: null);
        Assert.IsType<ClaudeAgentSetupStep>(step);
    }

    [Fact]
    public void Create_AgentClaude_CaseInsensitive_ReturnsClaudeAgentSetupStep()
    {
        var step = NewFactory().Create("CLAUDE", ghCliPathOverride: null);
        Assert.IsType<ClaudeAgentSetupStep>(step);
    }

    [Fact]
    public void Create_AgentCopilot_ReturnsCopilotAgentSetupStep()
    {
        var step = NewFactory().Create(CliArgumentParser.AgentCopilot, ghCliPathOverride: "/custom/gh");
        Assert.IsType<CopilotAgentSetupStep>(step);
    }

    [Fact]
    public void Create_NullAgent_DefaultsToCopilot()
    {
        var step = NewFactory().Create(agent: null, ghCliPathOverride: null);
        Assert.IsType<CopilotAgentSetupStep>(step);
    }

    [Fact]
    public void Create_UnknownAgent_DefaultsToCopilot()
    {
        var step = NewFactory().Create("gpt5", ghCliPathOverride: null);
        Assert.IsType<CopilotAgentSetupStep>(step);
    }
}
