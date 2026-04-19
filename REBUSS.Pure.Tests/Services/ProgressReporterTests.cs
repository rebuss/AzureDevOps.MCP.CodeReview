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
    private readonly CapturingLogger<ProgressReporter> _logger = new();
    private readonly ProgressReporter _reporter;

    public ProgressReporterTests()
    {
        _reporter = new ProgressReporter(_serviceProvider, _logger);
    }

    [Fact]
    public async Task ReportAsync_WithNullToken_LogsButDoesNotSendNotification()
    {
        await _reporter.ReportAsync(null, 1, 10, "Processing step 1/10");

        // Should log at Information level
        Assert.Contains(_logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("Processing step 1/10"));

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
        Assert.Contains(_logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("Step 5/10"));
    }

    [Fact]
    public async Task ReportAsync_AlwaysLogsRegardlessOfToken()
    {
        // null token
        await _reporter.ReportAsync(null, 0, 5, "Starting operation");

        Assert.Single(_logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("Starting operation"));
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
        Assert.Contains(_logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("Enriching files (2/5)"));
    }

    [Fact]
    public async Task ReportAsync_WithSdkProgress_NullTotal_ReportsCorrectly()
    {
        var sdkProgress = Substitute.For<IProgress<ProgressNotificationValue>>();

        await _reporter.ReportAsync(sdkProgress, 1, null, "Processing...");

        sdkProgress.Received(1).Report(Arg.Is<ProgressNotificationValue>(v =>
            v.Progress == 1 && v.Total == null && v.Message == "Processing..."));
    }

    /// <summary>
    /// Simple capturing logger that avoids the NSubstitute generic TState matching
    /// issue with <see cref="ILogger.Log{TState}"/>.
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }

        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    }
}
