using REBUSS.Pure.Cli;
using REBUSS.Pure.Services.ClaudeCode;

namespace REBUSS.Pure.Tests.Cli;

public class ClaudeCliSetupStepTests
{
    private sealed class FakeProbe(Queue<ClaudeVerdict> verdicts) : IClaudeVerificationProbe
    {
        public Task<ClaudeVerdict> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(verdicts.Dequeue());
    }

    /// <summary>
    /// Builds a <c>processRunner</c> that answers "installed" for the <c>claude</c>
    /// version probe and "not available" for every other executable. Lets tests focus
    /// on the verification/auth branches without a pre-install dance.
    /// </summary>
    private static Func<string, string, CancellationToken, Task<(int, string, string)>> ClaudeAlreadyInstalled() =>
        (exe, args, _) =>
        {
            if (exe == "claude" && args == "--version")
                return Task.FromResult((0, "claude-code 1.0", string.Empty));
            if (exe == "claude")
                return Task.FromResult((0, "{}", string.Empty));
            return Task.FromResult((-1, string.Empty, "not available"));
        };

    [Fact]
    public async Task RunAsync_ClaudeNotInstalled_UserDeclinesInstall_WritesDeclineBanner()
    {
        var output = new StringWriter();
        var input = new StringReader("n\n");
        Func<string, string, CancellationToken, Task<(int, string, string)>> runner =
            (_, _, _) => Task.FromResult((-1, string.Empty, "not found"));

        var step = new ClaudeCliSetupStep(output, input, processRunner: runner);

        await step.RunAsync();

        Assert.Contains("NOT CONFIGURED", output.ToString());
    }

    [Fact]
    public async Task RunAsync_ClaudeAlreadyInstalledAndAuthenticated_WritesVerifiedLine()
    {
        var output = new StringWriter();
        var input = new StringReader(string.Empty);
        var probe = new FakeProbe(new Queue<ClaudeVerdict>(new[]
        {
            new ClaudeVerdict(IsAvailable: true, Reason: "ok", Remediation: string.Empty)
        }));

        var step = new ClaudeCliSetupStep(output, input,
            processRunner: ClaudeAlreadyInstalled(),
            verificationProbe: probe);

        await step.RunAsync();

        var text = output.ToString();
        Assert.Contains("verified", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NOT CONFIGURED", text);
        Assert.DoesNotContain("NOT VERIFIED", text);
    }

    [Fact]
    public async Task RunAsync_AuthFailure_UserDeclinesLaunch_WritesAuthFailureBanner()
    {
        var output = new StringWriter();
        var input = new StringReader("n\n"); // decline "Launch Claude Code now?"
        var probe = new FakeProbe(new Queue<ClaudeVerdict>(new[]
        {
            new ClaudeVerdict(IsAvailable: false, Reason: "not-authenticated", Remediation: "Run /login.")
        }));

        var step = new ClaudeCliSetupStep(output, input,
            processRunner: ClaudeAlreadyInstalled(),
            verificationProbe: probe);

        await step.RunAsync();

        Assert.Contains("NOT VERIFIED", output.ToString());
    }

    [Fact]
    public async Task RunAsync_AuthFailureThenSuccess_WritesVerifiedLine()
    {
        var output = new StringWriter();
        var input = new StringReader("y\n"); // accept "Launch Claude Code now?"
        var probe = new FakeProbe(new Queue<ClaudeVerdict>(new[]
        {
            new ClaudeVerdict(IsAvailable: false, Reason: "not-authenticated", Remediation: "Run /login."),
            new ClaudeVerdict(IsAvailable: true, Reason: "ok", Remediation: string.Empty)
        }));

        var step = new ClaudeCliSetupStep(output, input,
            processRunner: ClaudeAlreadyInstalled(),
            verificationProbe: probe);

        await step.RunAsync();

        var text = output.ToString();
        Assert.Contains("verified", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NOT VERIFIED", text);
    }

    [Fact]
    public async Task RunAsync_ProbeNotConfigured_SilentlyReturns()
    {
        var output = new StringWriter();
        var input = new StringReader(string.Empty);

        // Claude installed, no probe → step should exit after install check without
        // a verification banner because there is nothing to verify against.
        var step = new ClaudeCliSetupStep(output, input,
            processRunner: ClaudeAlreadyInstalled(),
            verificationProbe: null);

        await step.RunAsync();

        var text = output.ToString();
        Assert.DoesNotContain("NOT CONFIGURED", text);
        Assert.DoesNotContain("NOT VERIFIED", text);
        Assert.DoesNotContain("verified", text, StringComparison.OrdinalIgnoreCase);
    }
}
