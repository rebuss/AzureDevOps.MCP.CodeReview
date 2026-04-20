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
}
