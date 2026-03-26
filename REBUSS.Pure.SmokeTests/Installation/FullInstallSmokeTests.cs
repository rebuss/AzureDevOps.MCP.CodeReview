using System.Text.Json;
using REBUSS.Pure.SmokeTests.Fixtures;

namespace REBUSS.Pure.SmokeTests.Installation;

/// <summary>
/// End-to-end installation test: pack → install as global tool → init → MCP handshake.
/// Simulates the real user installation flow.
/// </summary>
[Trait("Category", "Smoke")]
[Trait("Category", "SlowSmoke")]
public class FullInstallSmokeTests : IAsyncLifetime
{
#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif

    private string _nupkgDir = null!;
    private string _toolsDir = null!;
    private bool _toolInstalled;

    public async Task InitializeAsync()
    {
        _nupkgDir = Path.Combine(Path.GetTempPath(), "rebuss-smoke-pack-" + Guid.NewGuid().ToString("N")[..8]);
        _toolsDir = Path.Combine(Path.GetTempPath(), "rebuss-smoke-tools-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_nupkgDir);
        Directory.CreateDirectory(_toolsDir);

        var projectDir = ResolveProjectDirectory();

        // Pack the tool — use --no-build to avoid MSBuild contention with other
        // parallel smoke tests and to prevent background compiler server processes
        // (VBCSCompiler) from holding inherited pipe handles open.
        var packResult = await RunDotnetAsync($"pack \"{projectDir}\" -c {BuildConfiguration} --no-build -o \"{_nupkgDir}\"");
        Assert.Equal(0, packResult.ExitCode);

        // Install as a local tool in an isolated tool-path (avoids polluting global tools)
        var installResult = await RunDotnetAsync(
            $"tool install CodeReview.MCP --tool-path \"{_toolsDir}\" --add-source \"{_nupkgDir}\"");
        Assert.Equal(0, installResult.ExitCode);

        _toolInstalled = true;
    }

    public async Task DisposeAsync()
    {
        if (_toolInstalled)
        {
            await RunDotnetAsync($"tool uninstall CodeReview.MCP --tool-path \"{_toolsDir}\"");
        }

        TryDeleteDirectory(_nupkgDir);
        TryDeleteDirectory(_toolsDir);
    }

    [Fact]
    public async Task PackAndInstall_InitCreatesConfig_McpHandshakeSucceeds()
    {
        // Verify the tool is callable
        var exeName = OperatingSystem.IsWindows() ? "rebuss-pure.exe" : "rebuss-pure";
        var toolExe = Path.Combine(_toolsDir, exeName);
        Assert.True(File.Exists(toolExe), $"Expected tool binary at {toolExe}");

        // Run init in a temp git repo
        using var repo = TempGitRepoFixture.Create("https://github.com/smoke-test/repo.git");

        // Restrict PATH so that az/gh CLI tools are shadowed by exit-1 scripts.
        // On CI runners (Ubuntu), az CLI is pre-installed. If the tool shim doesn't
        // forward --pat correctly, the init command enters an interactive az-login
        // flow that blocks indefinitely. Shadowing az/gh ensures the "Install CLI?"
        // prompt appears instead, which the stdin "n\n" answer declines immediately.
        var initResult = await RunProcessAsync(toolExe, "init --pat smoke-test-token",
            workingDirectory: repo.RootPath, stdin: "n\n",
            timeout: TimeSpan.FromSeconds(60),
            environmentOverrides: CliProcessHelper.BuildRestrictedPathEnv());

        Assert.True(initResult.ExitCode == 0,
            $"init failed (exit {initResult.ExitCode}). stdout: {initResult.StdOut}\nstderr: {initResult.StdErr}");
        Assert.True(repo.FileExists(Path.Combine(".vscode", "mcp.json")));
        Assert.True(repo.FileExists(Path.Combine(".github", "prompts", "review-pr.md")));

        // Verify the config points to the installed tool
        var config = repo.ReadFile(Path.Combine(".vscode", "mcp.json"));
        Assert.Contains("REBUSS.Pure", config);
        Assert.Contains("--repo", config);

        // Start the installed tool as MCP server and perform a handshake
        repo.CreateFile("dummy.txt", "hello");
        RunGitInRepo(repo.RootPath, "add .");
        RunGitInRepo(repo.RootPath, "commit -m \"init\"");

        var serverResult = await McpHandshakeAsync(toolExe, repo.RootPath);
        Assert.True(serverResult.InitializeSucceeded, "MCP initialize handshake failed.");
        Assert.True(serverResult.ToolCount > 0, "Expected at least one tool in tools/list.");
    }

    private async Task<McpHandshakeResult> McpHandshakeAsync(string toolExe, string repoPath)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = toolExe,
            Arguments = $"--repo \"{repoPath}\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = System.Diagnostics.Process.Start(psi)!;

        try
        {
            var initRequest = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "1",
                method = "initialize",
                @params = new { }
            });

            var toolsListRequest = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "2",
                method = "tools/list"
            });

            try
            {
                await process.StandardInput.WriteLineAsync(initRequest);
                await process.StandardInput.WriteLineAsync(toolsListRequest);
                await process.StandardInput.FlushAsync();
            }
            catch (IOException) { }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            var initLine = await process.StandardOutput.ReadLineAsync(cts.Token);
            var toolsLine = await process.StandardOutput.ReadLineAsync(cts.Token);

            try { process.StandardInput.Close(); }
            catch (IOException) { }
            await process.WaitForExitAsync(cts.Token);

            var initOk = false;
            var toolCount = 0;

            if (!string.IsNullOrWhiteSpace(initLine))
            {
                var doc = JsonDocument.Parse(initLine);
                initOk = doc.RootElement.TryGetProperty("result", out var r)
                         && r.TryGetProperty("serverInfo", out _);
            }

            if (!string.IsNullOrWhiteSpace(toolsLine))
            {
                var doc = JsonDocument.Parse(toolsLine);
                if (doc.RootElement.TryGetProperty("result", out var r)
                    && r.TryGetProperty("tools", out var tools))
                {
                    toolCount = tools.GetArrayLength();
                }
            }

            return new McpHandshakeResult(initOk, toolCount);
        }
        catch
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            return new McpHandshakeResult(false, 0);
        }
    }

    private record McpHandshakeResult(bool InitializeSucceeded, int ToolCount);

    private static async Task<CliProcessResult> RunDotnetAsync(string arguments)
    {
        return await RunProcessAsync("dotnet", arguments);
    }

    private static async Task<CliProcessResult> RunProcessAsync(
        string fileName, string arguments,
        string? workingDirectory = null, string? stdin = null,
        TimeSpan? timeout = null,
        Dictionary<string, string>? environmentOverrides = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromMinutes(3);

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (workingDirectory is not null)
            psi.WorkingDirectory = workingDirectory;

        if (environmentOverrides is not null)
        {
            foreach (var (key, value) in environmentOverrides)
                psi.Environment[key] = value;
        }

        using var process = System.Diagnostics.Process.Start(psi)!;

        if (stdin is not null)
        {
            try
            {
                await process.StandardInput.WriteAsync(stdin);
                await process.StandardInput.FlushAsync();
            }
            catch (IOException)
            {
                // Process may have exited before consuming stdin — this is expected
                // for commands that finish without reading interactive input.
            }
        }

        try { process.StandardInput.Close(); }
        catch (IOException) { }

        // Start pipe reads immediately to prevent the process from blocking
        // when output exceeds the OS pipe buffer size.
        // Use a SEPARATE CTS for pipe reads — not tied to the process exit timeout.
        // Sharing a single CTS causes ReadToEndAsync to cancel simultaneously with
        // WaitForExitAsync, making partial output unrecoverable in the timeout handler.
        using var pipeCts = new CancellationTokenSource();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(pipeCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(pipeCts.Token);

        // WaitForExitAsync in .NET 7+ also waits for redirected-pipe EOF,
        // which can hang when child processes (tool shims, dotnet runtime)
        // inherit and hold pipe handles open.
        // Use WaitForExit(TimeSpan) to wait for the process exit signal only.
        var exited = await Task.Run(() => process.WaitForExit(effectiveTimeout));

        if (!exited)
        {
            process.Kill(entireProcessTree: true);
            // Give pipes a short grace period to drain after killing
            pipeCts.CancelAfter(TimeSpan.FromSeconds(3));
            string partialOut, partialErr;
            try { partialOut = await stdoutTask; } catch { partialOut = string.Empty; }
            try { partialErr = await stderrTask; } catch { partialErr = string.Empty; }

            return new CliProcessResult(-1, partialOut,
                $"Process timed out after {effectiveTimeout.TotalSeconds}s. stderr: {partialErr}");
        }

        // Process exited — drain pipes with a grace period.
        // If child processes keep handles open, reads will hang until cancelled.
        pipeCts.CancelAfter(TimeSpan.FromSeconds(5));
        string stdout, stderr;
        try
        {
            stdout = await stdoutTask;
            stderr = await stderrTask;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            stdout = string.Empty;
            stderr = string.Empty;
        }

        return new CliProcessResult(process.ExitCode, stdout, stderr);
    }

    private static string ResolveProjectDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "REBUSS.Pure", "REBUSS.Pure.csproj");
            if (File.Exists(candidate))
                return Path.Combine(dir.FullName, "REBUSS.Pure");
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate REBUSS.Pure project.");
    }

    private static void RunGitInRepo(string repoPath, string arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        process.WaitForExit(TimeSpan.FromSeconds(10));
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }
}
