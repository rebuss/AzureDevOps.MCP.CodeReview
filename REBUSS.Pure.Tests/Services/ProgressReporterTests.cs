using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
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

    [Fact]
    public async Task ReportAsync_WithSdkProgress_DelegatesToIProgressAndDoesNotResolveMcpServer()
    {
        var sdkProgress = Substitute.For<IProgress<ProgressNotificationValue>>();

        await _reporter.ReportAsync(sdkProgress, 2, 5, "Enriching files (2/5)");

        // Should delegate to IProgress<T>.Report()
        sdkProgress.Received(1).Report(Arg.Is<ProgressNotificationValue>(v =>
            v.Progress == 2 && v.Total == 5 && v.Message == "Enriching files (2/5)"));

        // Should NOT resolve McpServer — SDK path is self-contained
        _serviceProvider.DidNotReceive().GetService(typeof(McpServer));

        // Should still log
        _logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("Enriching files (2/5)")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task ReportAsync_WithSdkProgress_NullTotal_ReportsCorrectly()
    {
        var sdkProgress = Substitute.For<IProgress<ProgressNotificationValue>>();

        await _reporter.ReportAsync(sdkProgress, 1, null, "Processing...");

        sdkProgress.Received(1).Report(Arg.Is<ProgressNotificationValue>(v =>
            v.Progress == 1 && v.Total == null && v.Message == "Processing..."));
    }
}
