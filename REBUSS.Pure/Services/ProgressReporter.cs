using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using REBUSS.Pure.Core.Shared;

namespace REBUSS.Pure.Services;

/// <summary>
/// Sends MCP <c>notifications/progress</c> via the SDK transport and logs each step
/// at Information level. Resolves <see cref="McpServer"/> lazily from
/// <see cref="IServiceProvider"/> because it is not available in CLI mode.
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
