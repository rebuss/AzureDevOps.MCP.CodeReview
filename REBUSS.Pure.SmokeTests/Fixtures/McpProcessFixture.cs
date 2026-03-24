using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace REBUSS.Pure.SmokeTests.Fixtures;

/// <summary>
/// Starts <c>rebuss-pure</c> (via <c>dotnet run</c>) as a child process
/// and provides helpers to send JSON-RPC messages over stdin / read from stdout.
/// </summary>
public sealed class McpProcessFixture : IAsyncDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly Process _process;
    private readonly StringBuilder _stderrBuffer = new();
    private readonly Task _stderrDrainTask;

    private McpProcessFixture(Process process)
    {
        _process = process;

        // Drain stderr on a background thread to prevent pipe deadlocks.
        // Without this, the process can block writing to stderr (logging) which
        // then blocks stdout (JSON-RPC responses) due to full OS pipe buffers.
        _stderrDrainTask = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var line = await process.StandardError.ReadLineAsync();
                    if (line is null) break;
                    lock (_stderrBuffer) _stderrBuffer.AppendLine(line);
                }
            }
            catch { /* process exited */ }
        });
    }

    /// <summary>
    /// Launches the MCP server pointing at the given repository path.
    /// Uses <c>dotnet run --project</c> so no prior publish/pack step is required.
    /// </summary>
    public static McpProcessFixture Start(string repoPath, string? extraArgs = null)
    {
        var projectDir = ResolveProjectDirectory();

        var arguments = $"run --project \"{projectDir}\" --no-launch-profile --verbosity quiet -- --repo \"{repoPath}\"";
        if (!string.IsNullOrEmpty(extraArgs))
            arguments += " " + extraArgs;

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

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start MCP server process.");

        return new McpProcessFixture(process);
    }

    /// <summary>
    /// Sends a JSON-RPC request and returns the parsed response.
    /// </summary>
    public async Task<JsonDocument> SendAsync(object request, TimeSpan? timeout = null)
    {
        var json = JsonSerializer.Serialize(request);
        return await SendRawAsync(json, timeout);
    }

    /// <summary>
    /// Sends a raw JSON string and returns the parsed response.
    /// Skips any non-JSON lines (build output, log messages) from stdout.
    /// </summary>
    public async Task<JsonDocument> SendRawAsync(string json, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;

        await _process.StandardInput.WriteLineAsync(json);
        await _process.StandardInput.FlushAsync();

        using var cts = new CancellationTokenSource(effectiveTimeout);

        var line = await ReadJsonLineAsync(_process.StandardOutput, cts.Token);

        if (line is null)
            throw new TimeoutException($"No JSON response received within {effectiveTimeout.TotalSeconds}s.");

        return JsonDocument.Parse(line);
    }

    /// <summary>
    /// Sends multiple JSON-RPC requests (newline-separated) and returns all responses.
    /// </summary>
    public async Task<List<JsonDocument>> SendBatchAsync(IEnumerable<object> requests, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;
        var requestList = requests.ToList();

        foreach (var request in requestList)
        {
            var json = JsonSerializer.Serialize(request);
            await _process.StandardInput.WriteLineAsync(json);
        }
        await _process.StandardInput.FlushAsync();

        using var cts = new CancellationTokenSource(effectiveTimeout);
        var responses = new List<JsonDocument>();

        for (int i = 0; i < requestList.Count; i++)
        {
            var line = await ReadJsonLineAsync(_process.StandardOutput, cts.Token);
            if (line is null)
                break;
            responses.Add(JsonDocument.Parse(line));
        }

        return responses;
    }

    /// <summary>
    /// Closes stdin to signal the server to shut down, then waits for the process to exit.
    /// </summary>
    public async Task<int> ShutdownAsync(TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;

        _process.StandardInput.Close();

        using var cts = new CancellationTokenSource(effectiveTimeout);
        try
        {
            await _process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _process.Kill(entireProcessTree: true);
            return -1;
        }

        return _process.ExitCode;
    }

    /// <summary>
    /// Reads stderr output captured so far (useful for diagnostics on failure).
    /// </summary>
    public string GetStdErr()
    {
        lock (_stderrBuffer)
            return _stderrBuffer.ToString();
    }

    public async ValueTask DisposeAsync()
    {
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

            // Wait for stderr drain to complete
            await _stderrDrainTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        finally
        {
            _process.Dispose();
        }
    }

    /// <summary>
    /// Resolves the path to the REBUSS.Pure project directory.
    /// Works both from the IDE (running from SmokeTests bin/) and from the repo root.
    /// </summary>
    private static string ResolveProjectDirectory()
    {
        // Walk up from the test assembly location to find the solution root
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

    /// <summary>
    /// Reads lines from the stream, skipping any that don't start with '{' (non-JSON output).
    /// Strips UTF-8 BOM if present. Returns the first JSON line, or null if the stream ends.
    /// </summary>
    private static async Task<string?> ReadJsonLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                return null;

            var trimmed = line.TrimStart('\uFEFF').TrimStart();
            if (trimmed.StartsWith('{'))
                return trimmed;
        }
    }
}
