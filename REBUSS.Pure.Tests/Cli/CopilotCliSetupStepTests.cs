using REBUSS.Pure.Cli;

namespace REBUSS.Pure.Tests.Cli;

/// <summary>
/// Unit tests for <see cref="CopilotCliSetupStep"/> covering the ten scenarios from
/// <c>specs/012-copilot-cli-setup/spec.md</c> §Testing requirements.
/// The scripted <c>processRunner</c> delegate matches on the argument substring and
/// returns a canned <c>(ExitCode, StdOut, StdErr)</c> tuple.
/// </summary>
public class CopilotCliSetupStepTests
{
    private static Func<string, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>>
        Scripted(Func<string, (int, string, string)> reply) =>
        (args, _) => Task.FromResult(reply(args));

    private static (int, string, string) Ok(string stdout = "") => (0, stdout, string.Empty);
    private static (int, string, string) Fail(string stderr = "not found") => (-1, string.Empty, stderr);

    // ---------------------------------------------------------------
    // US1 — GitHub happy path: all already installed
    // ---------------------------------------------------------------

    [Fact]
    public async Task AllInstalled_PrintsConfirmation_NoPrompts()
    {
        var output = new StringWriter();
        var input = new StringReader(""); // must not be read
        var runner = Scripted(args =>
        {
            if (args.Contains("--version") && !args.Contains("copilot")) return Ok("gh 2.0");
            if (args.Contains("auth status")) return Ok("Logged in");
            if (args.Contains("copilot --version")) return Ok("copilot 1.0");
            return Ok();
        });

        var step = new CopilotCliSetupStep(output, input, runner);
        await step.RunAsync();

        Assert.Contains("already installed", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[y/N]", output.ToString());
    }

    [Fact]
    public async Task ExtensionMissing_UserAcceptsY_InstallsSuccessfully()
    {
        var output = new StringWriter();
        var input = new StringReader("y\n");
        var installCalled = false;
        var copilotInstalled = false;
        var runner = Scripted(args =>
        {
            if (args.Contains("--version") && !args.Contains("copilot")) return Ok("gh 2.0");
            if (args.Contains("auth status")) return Ok("Logged in");
            if (args.Contains("copilot --version")) return copilotInstalled ? Ok("copilot 1.0") : Fail();
            if (args.Contains("extension install")) { installCalled = true; copilotInstalled = true; return Ok(); }
            return Ok();
        });

        var step = new CopilotCliSetupStep(output, input, runner);
        await step.RunAsync();

        Assert.True(installCalled, "gh extension install github/gh-copilot was not invoked");
        Assert.Contains("installed successfully", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtensionMissing_UserDeclinesN_ShowsBanner()
    {
        var output = new StringWriter();
        var input = new StringReader("N\n");
        var installCalled = false;
        var runner = Scripted(args =>
        {
            if (args.Contains("--version") && !args.Contains("copilot")) return Ok("gh 2.0");
            if (args.Contains("auth status")) return Ok("Logged in");
            if (args.Contains("copilot --version")) return Fail();
            if (args.Contains("extension install")) { installCalled = true; return Ok(); }
            return Ok();
        });

        var step = new CopilotCliSetupStep(output, input, runner);
        await step.RunAsync();

        Assert.False(installCalled);
        Assert.Contains("GITHUB COPILOT CLI NOT CONFIGURED", output.ToString());
        Assert.Contains("gh extension install github/gh-copilot", output.ToString());
    }

    [Fact]
    public async Task DeclineOnce_ThenAccept_RePrompts()
    {
        // Two independent RunAsync calls simulate two init runs.
        // First run declines, second run must still prompt (Clarification Q1 — no decline memory).
        var copilotInstalled = false;
        var runner = Scripted(args =>
        {
            if (args.Contains("--version") && !args.Contains("copilot")) return Ok("gh 2.0");
            if (args.Contains("auth status")) return Ok("Logged in");
            if (args.Contains("copilot --version")) return copilotInstalled ? Ok("copilot 1.0") : Fail();
            if (args.Contains("extension install")) { copilotInstalled = true; return Ok(); }
            return Ok();
        });

        var output1 = new StringWriter();
        var step1 = new CopilotCliSetupStep(output1, new StringReader("N\n"), runner);
        await step1.RunAsync();
        Assert.Contains("NOT CONFIGURED", output1.ToString());

        copilotInstalled = false; // reset state for second run
        var output2 = new StringWriter();
        var step2 = new CopilotCliSetupStep(output2, new StringReader("y\n"), runner);
        await step2.RunAsync();
        Assert.Contains("[y/N]", output2.ToString()); // re-prompted
        Assert.Contains("installed successfully", output2.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------
    // US2 — gh missing / PATH override / not-authenticated recovery
    // ---------------------------------------------------------------

    [Fact]
    public async Task GhMissing_UserAcceptsY_InstallsLoginAndExtension_NoSecondExtensionPrompt()
    {
        var output = new StringWriter();
        var input = new StringReader("y\n"); // single "yes" for the entry prompt — must NOT be asked again
        var ghInstalled = false;
        var authed = false;
        var extensionInstallCalled = false;

        var runner = Scripted(args =>
        {
            if (args == "install-gh-cli") { ghInstalled = true; return Ok(); }
            if (args.Contains("--version") && !args.Contains("copilot"))
                return ghInstalled ? Ok("gh 2.0") : Fail();
            if (args.Contains("auth status"))
                return authed ? Ok("Logged in") : Fail("not authed");
            if (args.Contains("auth login")) { authed = true; return Ok(); }
            if (args.Contains("extension install")) { extensionInstallCalled = true; return Ok(); }
            if (args.Contains("copilot --version")) return extensionInstallCalled ? Ok("copilot 1.0") : Fail();
            return Ok();
        });

        var step = new CopilotCliSetupStep(output, input, runner);
        await step.RunAsync();

        Assert.True(ghInstalled);
        Assert.True(authed);
        Assert.True(extensionInstallCalled);
        // The entry prompt was shown exactly once — a second extension prompt would need a second "y",
        // but input only has one. If a second prompt were shown, ReadLine would return null (decline)
        // and install would NOT have been called. It was → Clarification Q2 honored.
        Assert.Contains("installed successfully", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GhInstalledButNotAuthenticated_LoginSucceeds_ProceedsToExtensionPrompt()
    {
        var output = new StringWriter();
        var input = new StringReader("y\ny\n"); // first "y" answers auth prompt, second answers extension prompt
        var authed = false;
        var copilotInstalled = false;
        var runner = Scripted(args =>
        {
            if (args.Contains("--version") && !args.Contains("copilot")) return Ok("gh 2.0");
            if (args.Contains("auth status"))
                return authed ? Ok("Logged in") : Fail("not authed");
            if (args.Contains("auth login")) { authed = true; return Ok(); }
            if (args.Contains("copilot --version")) return copilotInstalled ? Ok("copilot 1.0") : Fail();
            if (args.Contains("extension install")) { copilotInstalled = true; return Ok(); }
            return Ok();
        });

        var step = new CopilotCliSetupStep(output, input, runner);
        await step.RunAsync();

        Assert.Contains("installed successfully", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------
    // US3 — decline / failure / non-interactive all exit gracefully
    // ---------------------------------------------------------------

    [Fact]
    public async Task GhMissing_UserDeclinesN_SuppressesAllLaterPrompts()
    {
        var output = new StringWriter();
        // Only one line of input. If the step asks a second question, ReadLine returns null.
        var input = new StringReader("N\n");
        var installCalled = false;
        var runner = Scripted(args =>
        {
            if (args == "install-gh-cli") { installCalled = true; return Ok(); }
            if (args.Contains("--version") && !args.Contains("copilot")) return Fail();
            return Ok();
        });

        var step = new CopilotCliSetupStep(output, input, runner);
        await step.RunAsync();

        Assert.False(installCalled, "gh install must not be invoked after entry decline");
        Assert.Contains("NOT CONFIGURED", output.ToString());
    }

    [Fact]
    public async Task LoginFails_ShowsBanner_ReturnsGracefully()
    {
        var output = new StringWriter();
        var input = new StringReader("");
        var extensionInstallCalled = false;
        var runner = Scripted(args =>
        {
            if (args.Contains("--version") && !args.Contains("copilot")) return Ok("gh 2.0");
            if (args.Contains("auth status")) return Fail("not authed");
            if (args.Contains("auth login")) return (1, "", "login cancelled");
            if (args.Contains("extension install")) { extensionInstallCalled = true; return Ok(); }
            return Ok();
        });

        var step = new CopilotCliSetupStep(output, input, runner);
        await step.RunAsync();

        Assert.False(extensionInstallCalled);
        Assert.Contains("NOT CONFIGURED", output.ToString());
    }

    [Fact]
    public async Task ExtensionInstallFails_PrintsManualHintAndReturns()
    {
        var output = new StringWriter();
        var input = new StringReader("y\n");
        var runner = Scripted(args =>
        {
            if (args.Contains("--version") && !args.Contains("copilot")) return Ok("gh 2.0");
            if (args.Contains("auth status")) return Ok("Logged in");
            if (args.Contains("copilot --version")) return Fail();
            if (args.Contains("extension install")) return (1, "", "network error");
            return Ok();
        });

        var step = new CopilotCliSetupStep(output, input, runner);
        await step.RunAsync();

        Assert.Contains("installation failed", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gh extension install github/gh-copilot", output.ToString());
    }

    [Fact]
    public async Task EmptyStdin_TreatedAsDecline()
    {
        var output = new StringWriter();
        var input = new StringReader(""); // ReadLine will return null immediately
        var installCalled = false;
        var runner = Scripted(args =>
        {
            if (args.Contains("--version") && !args.Contains("copilot")) return Ok("gh 2.0");
            if (args.Contains("auth status")) return Ok("Logged in");
            if (args.Contains("copilot --version")) return Fail();
            if (args.Contains("extension install")) { installCalled = true; return Ok(); }
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
        // When a processRunner is supplied, the override is not observed in command args
        // (the runner is contract-free). This test simply verifies the constructor accepts
        // the override parameter and the step runs without throwing — the observable
        // effect of the override lives in the non-mocked code path.
        var output = new StringWriter();
        var input = new StringReader("");
        var runner = Scripted(args =>
        {
            if (args.Contains("--version") && !args.Contains("copilot")) return Ok("gh 2.0");
            if (args.Contains("auth status")) return Ok("Logged in");
            if (args.Contains("copilot --version")) return Ok("copilot 1.0");
            return Ok();
        });

        var step = new CopilotCliSetupStep(
            output, input, runner, ghCliPathOverride: @"C:\tmp\gh.exe");
        var ex = await Record.ExceptionAsync(() => step.RunAsync());

        Assert.Null(ex);
        Assert.Contains("already installed", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
