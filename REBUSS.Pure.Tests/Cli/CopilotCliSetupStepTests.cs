using NSubstitute;
using REBUSS.Pure.Cli;
using REBUSS.Pure.Core.Services.CopilotReview;
using REBUSS.Pure.Services.CopilotReview;

namespace REBUSS.Pure.Tests.Cli;

/// <summary>
/// Unit tests for <see cref="CopilotCliSetupStep"/>. After the Copilot CLI bundling
/// landed, the step no longer installs a <c>gh copilot</c> extension or triggers a
/// built-in download — it only makes sure <c>gh</c> itself is present and authenticated
/// with the Copilot scope, then delegates to the verification probe. These tests cover
/// that reduced surface.
/// </summary>
public class CopilotCliSetupStepTests
{
    private static Func<string, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>>
        Scripted(Func<string, (int, string, string)> reply) =>
        (args, _) => Task.FromResult(reply(args));

    private static (int, string, string) Ok(string stdout = "") => (0, stdout, string.Empty);
    private static (int, string, string) Fail(string stderr = "not found") => (-1, string.Empty, stderr);

    private static CopilotVerdict OkVerdict() => new(
        IsAvailable: true,
        Reason: CopilotAuthReason.Ok,
        TokenSource: CopilotTokenSource.LoggedInUser,
        ConfiguredModel: "claude-sonnet-4.6",
        EntitledModels: new[] { "claude-sonnet-4.6" },
        Login: "octocat",
        Host: "github.com",
        Remediation: string.Empty);

    // ---------------------------------------------------------------
    // Happy path — gh installed and authenticated
    // ---------------------------------------------------------------

    [Fact]
    public async Task GhInstalledAndAuthed_NoPrompts_CallsVerificationProbe()
    {
        var output = new StringWriter();
        var input = new StringReader("");
        var runner = Scripted(args =>
        {
            if (args.Contains("--version")) return Ok("gh 2.89");
            if (args.Contains("auth status")) return Ok("Logged in");
            return Ok();
        });
        var probe = Substitute.For<ICopilotVerificationProbe>();
        probe.ProbeAsync(Arg.Any<CancellationToken>()).Returns(OkVerdict());

        var step = new CopilotCliSetupStep(output, input, runner, verificationProbe: probe);
        await step.RunAsync();

        await probe.Received(1).ProbeAsync(Arg.Any<CancellationToken>());
        Assert.DoesNotContain("[y/N]", output.ToString());
        Assert.DoesNotContain("gh copilot", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------
    // gh missing — entry prompt drives the whole chain
    // ---------------------------------------------------------------

    [Fact]
    public async Task GhMissing_UserAcceptsY_InstallsGhLogsInAndVerifies()
    {
        var output = new StringWriter();
        var input = new StringReader("y\n"); // single "yes" for the entry prompt
        var ghInstalled = false;
        var authed = false;
        var installGhCalled = false;
        var loginCalled = false;

        var runner = Scripted(args =>
        {
            if (args == "install-gh-cli") { installGhCalled = true; ghInstalled = true; return Ok(); }
            if (args.Contains("--version")) return ghInstalled ? Ok("gh 2.89") : Fail();
            if (args.Contains("auth status")) return authed ? Ok("Logged in") : Fail("not authed");
            if (args.Contains("auth login")) { loginCalled = true; authed = true; return Ok(); }
            return Ok();
        });
        var probe = Substitute.For<ICopilotVerificationProbe>();
        probe.ProbeAsync(Arg.Any<CancellationToken>()).Returns(OkVerdict());

        var step = new CopilotCliSetupStep(output, input, runner, verificationProbe: probe);
        await step.RunAsync();

        Assert.True(installGhCalled);
        Assert.True(loginCalled);
        await probe.Received(1).ProbeAsync(Arg.Any<CancellationToken>());
        // Exactly one prompt — no second prompt for an extension
        Assert.DoesNotContain("extension", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GhMissing_UserDeclinesN_ShowsBannerAndSkipsInstall()
    {
        var output = new StringWriter();
        var input = new StringReader("N\n");
        var installCalled = false;
        var runner = Scripted(args =>
        {
            if (args == "install-gh-cli") { installCalled = true; return Ok(); }
            if (args.Contains("--version")) return Fail();
            return Ok();
        });
        var probe = Substitute.For<ICopilotVerificationProbe>();

        var step = new CopilotCliSetupStep(output, input, runner, verificationProbe: probe);
        await step.RunAsync();

        Assert.False(installCalled);
        await probe.DidNotReceive().ProbeAsync(Arg.Any<CancellationToken>());
        Assert.Contains("NOT CONFIGURED", output.ToString());
    }

    [Fact]
    public async Task GhMissing_InstallSucceedsButAuthDeclined_ShowsBanner()
    {
        // Login path is non-interactive from the step's perspective once consent has been
        // given at the entry prompt — a failing `auth login` returns non-zero and we bail.
        var output = new StringWriter();
        var input = new StringReader("y\n");
        var ghInstalled = false;
        var runner = Scripted(args =>
        {
            if (args == "install-gh-cli") { ghInstalled = true; return Ok(); }
            if (args.Contains("--version")) return ghInstalled ? Ok("gh 2.89") : Fail();
            if (args.Contains("auth status")) return Fail("not authed");
            if (args.Contains("auth login")) return (1, "", "login cancelled");
            return Ok();
        });
        var probe = Substitute.For<ICopilotVerificationProbe>();

        var step = new CopilotCliSetupStep(output, input, runner, verificationProbe: probe);
        await step.RunAsync();

        await probe.DidNotReceive().ProbeAsync(Arg.Any<CancellationToken>());
        Assert.Contains("NOT CONFIGURED", output.ToString());
    }

    // ---------------------------------------------------------------
    // gh installed but not authenticated
    // ---------------------------------------------------------------

    [Fact]
    public async Task GhInstalledNotAuthed_UserAcceptsY_LoginThenVerifies()
    {
        var output = new StringWriter();
        var input = new StringReader("y\n");
        var authed = false;
        var loginCalled = false;
        var runner = Scripted(args =>
        {
            if (args.Contains("--version")) return Ok("gh 2.89");
            if (args.Contains("auth status")) return authed ? Ok("Logged in") : Fail("not authed");
            if (args.Contains("auth login")) { loginCalled = true; authed = true; return Ok(); }
            return Ok();
        });
        var probe = Substitute.For<ICopilotVerificationProbe>();
        probe.ProbeAsync(Arg.Any<CancellationToken>()).Returns(OkVerdict());

        var step = new CopilotCliSetupStep(output, input, runner, verificationProbe: probe);
        await step.RunAsync();

        Assert.True(loginCalled);
        await probe.Received(1).ProbeAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GhInstalledNotAuthed_UserDeclinesN_ShowsBanner()
    {
        var output = new StringWriter();
        var input = new StringReader("N\n");
        var loginCalled = false;
        var runner = Scripted(args =>
        {
            if (args.Contains("--version")) return Ok("gh 2.89");
            if (args.Contains("auth status")) return Fail("not authed");
            if (args.Contains("auth login")) { loginCalled = true; return Ok(); }
            return Ok();
        });
        var probe = Substitute.For<ICopilotVerificationProbe>();

        var step = new CopilotCliSetupStep(output, input, runner, verificationProbe: probe);
        await step.RunAsync();

        Assert.False(loginCalled);
        await probe.DidNotReceive().ProbeAsync(Arg.Any<CancellationToken>());
        Assert.Contains("NOT CONFIGURED", output.ToString());
    }

    [Fact]
    public async Task GhInstalledNotAuthed_LoginReturnsNonZero_ShowsBannerNoVerification()
    {
        var output = new StringWriter();
        var input = new StringReader("y\n");
        var runner = Scripted(args =>
        {
            if (args.Contains("--version")) return Ok("gh 2.89");
            if (args.Contains("auth status")) return Fail("not authed");
            if (args.Contains("auth login")) return (1, "", "login cancelled");
            return Ok();
        });
        var probe = Substitute.For<ICopilotVerificationProbe>();

        var step = new CopilotCliSetupStep(output, input, runner, verificationProbe: probe);
        await step.RunAsync();

        await probe.DidNotReceive().ProbeAsync(Arg.Any<CancellationToken>());
        Assert.Contains("NOT CONFIGURED", output.ToString());
    }

    // ---------------------------------------------------------------
    // Edge cases
    // ---------------------------------------------------------------

    [Fact]
    public async Task EmptyStdin_TreatedAsDecline_AtEntryPrompt()
    {
        var output = new StringWriter();
        var input = new StringReader(""); // ReadLine returns null → decline
        var installCalled = false;
        var runner = Scripted(args =>
        {
            if (args == "install-gh-cli") { installCalled = true; return Ok(); }
            if (args.Contains("--version")) return Fail();
            return Ok();
        });

        var step = new CopilotCliSetupStep(output, input, runner);
        await step.RunAsync();

        Assert.False(installCalled);
        Assert.Contains("NOT CONFIGURED", output.ToString());
    }

    [Fact]
    public async Task GhCliPathOverride_IsAccepted_ViaConstructor()
    {
        // The override value is opaque to the scripted runner (runner ignores the override
        // and is keyed only on arg-substring). We simply assert that the constructor accepts
        // the parameter and the step completes without throwing.
        var output = new StringWriter();
        var input = new StringReader("");
        var runner = Scripted(args =>
        {
            if (args.Contains("--version")) return Ok("gh 2.89");
            if (args.Contains("auth status")) return Ok("Logged in");
            return Ok();
        });

        var step = new CopilotCliSetupStep(
            output, input, runner, ghCliPathOverride: @"C:\tmp\gh.exe");
        var ex = await Record.ExceptionAsync(() => step.RunAsync());

        Assert.Null(ex);
    }
}
