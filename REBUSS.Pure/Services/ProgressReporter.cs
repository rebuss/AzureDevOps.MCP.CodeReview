using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using REBUSS.Pure.Core.Shared;

namespace REBUSS.Pure.Services;

/// <summary>
/// Sends MCP <c>notifications/progress</c> via the SDK transport and logs each step
/// at Information level. Supports two dispatch paths:
/// <list type="number">
/// <item>SDK-injected <see cref="IProgress{ProgressNotificationValue}"/> — preferred;
/// the SDK handles token extraction and notification dispatch automatically.</item>
/// <item>Legacy <see cref="ProgressToken"/> — resolves <see cref="McpServer"/> lazily
/// from <see cref="IServiceProvider"/> (unavailable in CLI mode).</item>
/// </list>
/// </summary>
public sealed class ProgressReporter : IProgressReporter
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ProgressReporter> _logger;

    public ProgressReporter(IServiceProvider serviceProvider, ILogger<ProgressReporter> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task ReportAsync(
        object? progressToken,
        int progress,
        int? total,
        string message,
        CancellationToken cancellationToken = default)
    {
        // Always log regardless of token (FR-006)
        _logger.LogInformation("{ProgressMessage}", message);

        // Preferred path: SDK-injected IProgress<T> handles token extraction and
        // notification dispatch automatically. No-ops when client omits progressToken.
        if (progressToken is IProgress<ProgressNotificationValue> sdkProgress)
        {
            sdkProgress.Report(new ProgressNotificationValue
            {
                Progress = progress,
                Total = total,
                Message = message,
            });
            return;
        }

        // Legacy fallback: explicit ProgressToken + manual McpServer resolution.
        if (progressToken is not ProgressToken token)
            return;

        var mcpServer = _serviceProvider.GetService(typeof(McpServer)) as McpServer;
        if (mcpServer is null)
            return;

        try
        {
            await mcpServer.SendNotificationAsync(
                "notifications/progress",
                new ProgressNotificationParams
                {
                    ProgressToken = token,
                    Progress = new ProgressNotificationValue
                    {
                        Progress = progress,
                        Total = total,
                        Message = message,
                    },
                },
                serializerOptions: null,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            // Graceful degradation — notification failure must not break the operation
            _logger.LogDebug(ex, "Failed to send progress notification");
        }
    }
}
