using REBUSS.Pure.Services.ClaudeCode;

namespace REBUSS.Pure.Tests.Services.ClaudeCode;

public class ClaudeVerificationRunnerTests
{
    [Fact]
    public async Task ProbeAsync_ValidJsonResult_ReturnsAvailable()
    {
        Func<string, CancellationToken, Task<(int, string, string)>> runner =
            (_, _) => Task.FromResult((0, "{\"result\": \"ok\", \"session_id\": \"abc\"}", string.Empty));

        var probe = new ClaudeVerificationRunner(processRunner: runner);

        var verdict = await probe.ProbeAsync(CancellationToken.None);

        Assert.True(verdict.IsAvailable);
        Assert.Equal("ok", verdict.Reason);
    }

    [Fact]
    public async Task ProbeAsync_ExitNonZeroWithAuthHint_ReturnsNotAuthenticated()
    {
        Func<string, CancellationToken, Task<(int, string, string)>> runner =
            (_, _) => Task.FromResult((1, string.Empty, "You are not logged in. Run /login."));

        var probe = new ClaudeVerificationRunner(processRunner: runner);

        var verdict = await probe.ProbeAsync(CancellationToken.None);

        Assert.False(verdict.IsAvailable);
        Assert.Equal("not-authenticated", verdict.Reason);
        Assert.Contains("/login", verdict.Remediation);
    }

    [Fact]
    public async Task ProbeAsync_ExitZeroButInvalidJson_ReturnsInvalidResponse()
    {
        Func<string, CancellationToken, Task<(int, string, string)>> runner =
            (_, _) => Task.FromResult((0, "not json at all", string.Empty));

        var probe = new ClaudeVerificationRunner(processRunner: runner);

        var verdict = await probe.ProbeAsync(CancellationToken.None);

        Assert.False(verdict.IsAvailable);
        Assert.Equal("invalid-response", verdict.Reason);
    }

    [Fact]
    public async Task ProbeAsync_ExitZeroJsonMissingResult_ReturnsInvalidResponse()
    {
        Func<string, CancellationToken, Task<(int, string, string)>> runner =
            (_, _) => Task.FromResult((0, "{\"session_id\": \"abc\"}", string.Empty));

        var probe = new ClaudeVerificationRunner(processRunner: runner);

        var verdict = await probe.ProbeAsync(CancellationToken.None);

        Assert.False(verdict.IsAvailable);
        Assert.Equal("invalid-response", verdict.Reason);
    }

    [Fact]
    public async Task ProbeAsync_ExitNonZeroWithoutAuthHint_ReturnsError()
    {
        Func<string, CancellationToken, Task<(int, string, string)>> runner =
            (_, _) => Task.FromResult((127, string.Empty, "segfault"));

        var probe = new ClaudeVerificationRunner(processRunner: runner);

        var verdict = await probe.ProbeAsync(CancellationToken.None);

        Assert.False(verdict.IsAvailable);
        Assert.Equal("error", verdict.Reason);
    }

    [Fact]
    public async Task ProbeAsync_NoApiKey_OmitsBareFlag()
    {
        // Without ANTHROPIC_API_KEY the probe must not use --bare, otherwise it
        // would incorrectly report a logged-in subscription user as unauthenticated.
        string? capturedArgs = null;
        Func<string, CancellationToken, Task<(int, string, string)>> runner =
            (args, _) => { capturedArgs = args; return Task.FromResult((0, "{\"result\":\"pong\"}", string.Empty)); };

        var probe = new ClaudeVerificationRunner(processRunner: runner, environmentLookup: _ => null);

        await probe.ProbeAsync(CancellationToken.None);

        Assert.NotNull(capturedArgs);
        Assert.DoesNotContain("--bare", capturedArgs);
    }

    [Fact]
    public async Task ProbeAsync_ApiKeySet_AddsBareFlag()
    {
        string? capturedArgs = null;
        Func<string, CancellationToken, Task<(int, string, string)>> runner =
            (args, _) => { capturedArgs = args; return Task.FromResult((0, "{\"result\":\"pong\"}", string.Empty)); };

        Func<string, string?> env = name => name == "ANTHROPIC_API_KEY" ? "sk-ant-xyz" : null;
        var probe = new ClaudeVerificationRunner(processRunner: runner, environmentLookup: env);

        await probe.ProbeAsync(CancellationToken.None);

        Assert.NotNull(capturedArgs);
        Assert.Contains("--bare", capturedArgs);
    }

    [Fact]
    public async Task ProbeAsync_ApiKeySet_AuthFailure_RemediationMentionsApiKey()
    {
        Func<string, CancellationToken, Task<(int, string, string)>> runner =
            (_, _) => Task.FromResult((1, "{\"result\":\"Not logged in · Please run /login\"}", string.Empty));

        Func<string, string?> env = name => name == "ANTHROPIC_API_KEY" ? "sk-ant-bad" : null;
        var probe = new ClaudeVerificationRunner(processRunner: runner, environmentLookup: env);

        var verdict = await probe.ProbeAsync(CancellationToken.None);

        Assert.False(verdict.IsAvailable);
        Assert.Equal("not-authenticated", verdict.Reason);
        Assert.Contains("ANTHROPIC_API_KEY", verdict.Remediation);
    }

    [Fact]
    public async Task ProbeAsync_NoApiKey_AuthFailure_RemediationMentionsLogin()
    {
        Func<string, CancellationToken, Task<(int, string, string)>> runner =
            (_, _) => Task.FromResult((1, string.Empty, "You are not logged in. Run /login."));

        var probe = new ClaudeVerificationRunner(processRunner: runner, environmentLookup: _ => null);

        var verdict = await probe.ProbeAsync(CancellationToken.None);

        Assert.False(verdict.IsAvailable);
        Assert.Equal("not-authenticated", verdict.Reason);
        Assert.Contains("/login", verdict.Remediation);
    }
}
