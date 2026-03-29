using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.AzureDevOps.Properties;

namespace REBUSS.Pure.AzureDevOps.Configuration;

/// <summary>
/// Acquires an Azure DevOps access token by shelling out to <c>az account get-access-token</c>.
/// The Azure DevOps resource ID (<c>499b84ac-1321-427f-aa17-267ca6975798</c>) is used to
/// request a token scoped to Azure DevOps.
/// </summary>
public class AzureCliTokenProvider : IAzureCliTokenProvider
{
    /// <summary>
    /// The well-known resource ID for Azure DevOps.
    /// </summary>
    internal const string AzureDevOpsResourceId = "499b84ac-1321-427f-aa17-267ca6975798";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogger<AzureCliTokenProvider> _logger;

    public AzureCliTokenProvider(ILogger<AzureCliTokenProvider> logger)
    {
        _logger = logger;
    }

    public async Task<AzureCliToken?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await RunAzCliAsync(
                string.Format(Resources.AzCliGetTokenArgsTemplate, AzureDevOpsResourceId),
                cancellationToken);

            if (string.IsNullOrWhiteSpace(json))
            {
                _logger.LogDebug("Azure CLI returned empty output");
                return null;
            }

            return ParseTokenResponse(json);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to acquire token via Azure CLI");
            return null;
        }
    }

    internal static AzureCliToken? ParseTokenResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("accessToken", out var tokenEl))
            return null;

        var accessToken = tokenEl.GetString();
        if (string.IsNullOrWhiteSpace(accessToken))
            return null;

        DateTime expiresOn = DateTime.UtcNow.AddHours(1); // default fallback
        if (root.TryGetProperty("expiresOn", out var expiresOnEl))
        {
            var expiresOnStr = expiresOnEl.GetString();
            if (expiresOnStr is not null && DateTime.TryParse(expiresOnStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                expiresOn = parsed.ToUniversalTime();
        }

        return new AzureCliToken(accessToken, expiresOn);
    }

    private async Task<string?> RunAzCliAsync(string arguments, CancellationToken cancellationToken)
    {
        var (fileName, args) = AzureCliProcessHelper.GetProcessStartArgs(arguments);

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
            _logger.LogDebug("Failed to start Azure CLI process");
            return null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(CommandTimeout);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
        await Task.WhenAll(stdoutTask, stderrTask);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        await process.WaitForExitAsync(cts.Token);

        if (process.ExitCode != 0)
        {
            _logger.LogDebug("Azure CLI exited with code {ExitCode}: {StdErr}",
                process.ExitCode, stderr.Trim());
            return null;
        }

        return stdout;
    }
}
