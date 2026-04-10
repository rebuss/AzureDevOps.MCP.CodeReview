using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NSubstitute;
using REBUSS.Pure.Services;

namespace REBUSS.Pure.Tests.Services;

public class ProgressReporterTests
{
    private readonly IServiceProvider _serviceProvider = Substitute.For<IServiceProvider>();
    private readonly ILogger<ProgressReporter> _logger = Substitute.For<ILogger<ProgressReporter>>();
    private readonly ProgressReporter _reporter;

    public ProgressReporterTests()
    {
        _logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        _reporter = new ProgressReporter(_serviceProvider, _logger);
    }

    [Fact]
    public async Task ReportAsync_WithNullToken_LogsButDoesNotSendNotification()
    {
        await _reporter.ReportAsync(null, 1, 10, "Processing step 1/10");

        // Should log at Information level
        _logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Processing step 1/10")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());

        // Should NOT try to resolve McpServer (no token = no notification)
        _serviceProvider.DidNotReceive().GetService(typeof(McpServer));
    }

    [Fact]
    public async Task ReportAsync_WithToken_McpServerUnavailable_LogsWithoutException()
    {
        var token = new ProgressToken("test-token");
        _serviceProvider.GetService(typeof(McpServer)).Returns(null as McpServer);

        var ex = await Record.ExceptionAsync(
            () => _reporter.ReportAsync(token, 5, 10, "Step 5/10"));

        Assert.Null(ex);

        // Still logs
        _logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Step 5/10")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task ReportAsync_AlwaysLogsRegardlessOfToken()
    {
        // null token
        await _reporter.ReportAsync(null, 0, 5, "Starting operation");

        _logger.Received(1).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Starting operation")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}
