using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace REBUSS.Pure.SmokeTests.Infrastructure;

/// <summary>
/// Starts <c>REBUSS.Pure</c> as a child process with provider-specific CLI arguments
/// and provides helpers to send JSON-RPC tool calls over stdin / read from stdout.
/// Designed for contract tests that hit real APIs.
/// </summary>
public sealed class ContractMcpProcessFixture : IAsyncLifetime
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly string _cliArgs;
    private Process? _process;
    private StringBuilder? _stderrBuffer;
    private Task? _stderrDrainTask;
    private bool _initialized;
    private int _requestId;

    private ContractMcpProcessFixture(string cliArgs)
    {
        _cliArgs = cliArgs;
    }

    /// <summary>
    /// Creates a fixture configured for Azure DevOps.
    /// </summary>
    public static ContractMcpProcessFixture ForAzureDevOps(
        string pat, string org, string project, string repository)
    {
        var args = $"--provider AzureDevOps --pat \"{pat}\" --org \"{org}\" --project \"{project}\" --repository \"{repository}\"";
        return new ContractMcpProcessFixture(args);
    }

    /// <summary>
    /// Creates a fixture configured for GitHub.
    /// </summary>
    public static ContractMcpProcessFixture ForGitHub(
        string pat, string owner, string repository)
    {
        var args = $"--provider GitHub --pat \"{pat}\" --owner \"{owner}\" --repository \"{repository}\"";
        return new ContractMcpProcessFixture(args);
    }

    /// <summary>
    /// Creates a fixture with no provider credentials (protocol-only tests).
    /// </summary>
    public static ContractMcpProcessFixture ForProtocol()
    {
        return new ContractMcpProcessFixture(string.Empty);
    }

    public async Task InitializeAsync()
    {
        var projectDir = ResolveProjectDirectory();
        var repoPath = ResolveSolutionRoot();

        var arguments = $"run --project \"{projectDir}\" --no-launch-profile --verbosity quiet -- --repo \"{repoPath}\"";
        if (!string.IsNullOrEmpty(_cliArgs))
            arguments += " " + _cliArgs;

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start MCP server process.");

        _stderrBuffer = new StringBuilder();
        _stderrDrainTask = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var line = await _process.StandardError.ReadLineAsync();
                    if (line is null) break;
                    lock (_stderrBuffer) _stderrBuffer.AppendLine(line);
                }
            }
            catch { /* process exited */ }
        });

        // Perform MCP handshake
        var initResponse = await SendRawRequestAsync("initialize", new { });
        var result = initResponse.RootElement.GetProperty("result");
        if (!result.TryGetProperty("serverInfo", out _))
            throw new InvalidOperationException("MCP initialize handshake failed.");

        _initialized = true;
    }

    public async Task DisposeAsync()
    {
        if (_process is null) return;

        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await _process.WaitForExitAsync(cts.Token);
                }
                catch
                {
                    _process.Kill(entireProcessTree: true);
                }
            }

            if (_stderrDrainTask is not null)
                await _stderrDrainTask.WaitAsync(TimeSpan.FromSeconds(2))
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        finally
        {
            _process.Dispose();
        }
    }

    /// <summary>
    /// Sends a <c>tools/call</c> JSON-RPC request and returns the parsed result.
    /// </summary>
    public async Task<JsonDocument> SendToolCallAsync(
        string toolName,
        object arguments,
        TimeSpan? timeout = null)
    {
        if (!_initialized)
            throw new InvalidOperationException("Fixture not initialized. Call InitializeAsync first.");

        return await SendRawRequestAsync("tools/call",
            new { name = toolName, arguments },
            timeout);
    }

    /// <summary>
    /// Sends a <c>tools/list</c> JSON-RPC request and returns the parsed result.
    /// </summary>
    public async Task<JsonDocument> SendToolsListAsync(TimeSpan? timeout = null)
    {
        if (!_initialized)
            throw new InvalidOperationException("Fixture not initialized. Call InitializeAsync first.");

        return await SendRawRequestAsync("tools/list", null, timeout);
    }

    /// <summary>
    /// Returns the initialize response captured during <see cref="InitializeAsync"/>.
    /// Re-sends the request (safe — MCP allows it).
    /// </summary>
    public async Task<JsonDocument> SendInitializeAsync(TimeSpan? timeout = null)
    {
        return await SendRawRequestAsync("initialize", new { }, timeout);
    }

    public string GetStdErr()
    {
        if (_stderrBuffer is null) return string.Empty;
        lock (_stderrBuffer) return _stderrBuffer.ToString();
    }

    private async Task<JsonDocument> SendRawRequestAsync(
        string method, object? @params, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;
        var id = $"ct-{Interlocked.Increment(ref _requestId)}";

        var request = @params is not null
            ? new { jsonrpc = "2.0", id, method, @params }
            : (object)new { jsonrpc = "2.0", id, method };

        var json = JsonSerializer.Serialize(request);

        await _process!.StandardInput.WriteLineAsync(json);
        await _process.StandardInput.FlushAsync();

        using var cts = new CancellationTokenSource(effectiveTimeout);
        var line = await ReadJsonLineAsync(_process.StandardOutput, cts.Token);

        if (line is null)
        {
            var stderr = GetStdErr();
            throw new TimeoutException(
                $"No JSON response within {effectiveTimeout.TotalSeconds}s for '{method}'.\nStdErr:\n{stderr}");
        }

        return JsonDocument.Parse(line);
    }

    private static async Task<string?> ReadJsonLineAsync(StreamReader reader, CancellationToken ct)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) return null;

            var trimmed = line.TrimStart('\uFEFF').TrimStart();
            if (trimmed.StartsWith('{'))
                return trimmed;
        }
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

        throw new InvalidOperationException(
            "Could not locate REBUSS.Pure project. Ensure the solution structure is intact.");
    }

    private static string ResolveSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "REBUSS.Pure", "REBUSS.Pure.csproj");
            if (File.Exists(candidate))
                return dir.FullName;
            dir = dir.Parent;
        }

        return AppContext.BaseDirectory;
    }
}
