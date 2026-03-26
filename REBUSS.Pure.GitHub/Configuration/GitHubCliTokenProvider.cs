using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace REBUSS.Pure.GitHub.Configuration;

/// <summary>
/// Acquires a GitHub access token by shelling out to <c>gh auth token</c>.
/// The GitHub CLI returns the token as plain text on stdout.
/// </summary>
public class GitHubCliTokenProvider : IGitHubCliTokenProvider
{
    /// <summary>
    /// GitHub CLI tokens do not carry an explicit expiry. We use a 24-hour window
    /// so the cached token is periodically refreshed, mitigating the risk of using
    /// a revoked token for an extended period.
    /// </summary>
    internal static readonly TimeSpan DefaultTokenLifetime = TimeSpan.FromHours(24);

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogger<GitHubCliTokenProvider> _logger;

    public GitHubCliTokenProvider(ILogger<GitHubCliTokenProvider> logger)
    {
        _logger = logger;
    }

    public async Task<GitHubCliToken?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var output = await RunGhCliAsync("auth token", cancellationToken);
            return ParseTokenResponse(output);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to acquire token via GitHub CLI");
            return null;
        }
    }

    /// <summary>
    /// Parses the plain-text output of <c>gh auth token</c>.
    /// Returns <c>null</c> if the output is empty or whitespace.
    /// </summary>
    internal static GitHubCliToken? ParseTokenResponse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        var token = output.Trim();
        return new GitHubCliToken(token, DateTime.UtcNow.Add(DefaultTokenLifetime));
    }

    private async Task<string?> RunGhCliAsync(string arguments, CancellationToken cancellationToken)
    {
        var (fileName, args) = GitHubCliProcessHelper.GetProcessStartArgs(arguments);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            _logger.LogDebug("Failed to start GitHub CLI process");
            return null;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(CommandTimeout);

        var stdout = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        await process.StandardError.ReadToEndAsync(timeoutCts.Token);
        await process.WaitForExitAsync(timeoutCts.Token);

        if (process.ExitCode != 0)
        {
            _logger.LogDebug("GitHub CLI exited with code {ExitCode}", process.ExitCode);
            return null;
        }

        return stdout;
    }
}
