using REBUSS.Pure.Services.AgentInvocation;

namespace REBUSS.Pure.Tests.Services.AgentInvocation;

public class ClaudeCliAgentInvokerTests
{
    [Fact]
    public void ExtractResultFromJson_ValidResponse_ReturnsResultField()
    {
        const string stdout = """{"result": "page review text here", "session_id": "abc"}""";

        var result = ClaudeCliAgentInvoker.ExtractResultFromJson(stdout);

        Assert.Equal("page review text here", result);
    }

    [Fact]
    public void ExtractResultFromJson_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ClaudeCliAgentInvoker.ExtractResultFromJson(string.Empty));
        Assert.Equal(string.Empty, ClaudeCliAgentInvoker.ExtractResultFromJson("   "));
    }

    [Fact]
    public void ExtractResultFromJson_MissingResultField_FallsBackToRawStdout()
    {
        const string stdout = """{"session_id": "abc"}""";

        var result = ClaudeCliAgentInvoker.ExtractResultFromJson(stdout);

        Assert.Equal(stdout, result);
    }

    [Fact]
    public void ExtractResultFromJson_InvalidJson_FallsBackToRawStdout()
    {
        const string stdout = "not json at all";

        var result = ClaudeCliAgentInvoker.ExtractResultFromJson(stdout);

        Assert.Equal(stdout, result);
    }

    [Fact]
    public void ExtractResultFromJson_ResultIsNonString_FallsBackToRawStdout()
    {
        const string stdout = """{"result": 42}""";

        var result = ClaudeCliAgentInvoker.ExtractResultFromJson(stdout);

        Assert.Equal(stdout, result);
    }
}
