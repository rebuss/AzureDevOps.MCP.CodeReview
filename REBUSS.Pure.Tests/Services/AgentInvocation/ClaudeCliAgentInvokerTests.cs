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

    [Fact]
    public void ShouldUseBareMode_ApiKeySet_ReturnsTrue()
    {
        Func<string, string?> env = name => name == "ANTHROPIC_API_KEY" ? "sk-ant-xyz" : null;

        Assert.True(ClaudeCliAgentInvoker.ShouldUseBareMode(env));
    }

    [Fact]
    public void ShouldUseBareMode_ApiKeyMissingOrBlank_ReturnsFalse()
    {
        Func<string, string?> none = _ => null;
        Func<string, string?> blank = _ => "   ";

        Assert.False(ClaudeCliAgentInvoker.ShouldUseBareMode(none));
        Assert.False(ClaudeCliAgentInvoker.ShouldUseBareMode(blank));
    }

    [Fact]
    public void ShouldUseBareMode_OnlyOauthTokenSet_ReturnsFalse()
    {
        // CLAUDE_CODE_OAUTH_TOKEN must NOT trigger bare mode — bare mode ignores it,
        // so enabling --bare here would break a perfectly valid subscription auth path.
        Func<string, string?> env = name => name == "CLAUDE_CODE_OAUTH_TOKEN" ? "tok_abc" : null;

        Assert.False(ClaudeCliAgentInvoker.ShouldUseBareMode(env));
    }

    [Fact]
    public void FormatFailureDetail_BothEmpty_ReportsNoOutput()
    {
        Assert.Equal("no output captured.",
            ClaudeCliAgentInvoker.FormatFailureDetail(string.Empty, string.Empty));
    }

    [Fact]
    public void FormatFailureDetail_OnlyStdout_IncludesStdout()
    {
        // Regression guard: claude -p reports auth/quota errors on stdout (JSON payload),
        // not stderr. The previous failure message logged only stderr — silent failure.
        var detail = ClaudeCliAgentInvoker.FormatFailureDetail(
            "{\"result\":\"Not logged in\"}", string.Empty);

        Assert.Contains("Not logged in", detail);
        Assert.StartsWith("stdout:", detail);
    }

    [Fact]
    public void FormatFailureDetail_BothPresent_IncludesBoth()
    {
        var detail = ClaudeCliAgentInvoker.FormatFailureDetail("body", "boom");

        Assert.Contains("stdout: body", detail);
        Assert.Contains("stderr: boom", detail);
    }
}
