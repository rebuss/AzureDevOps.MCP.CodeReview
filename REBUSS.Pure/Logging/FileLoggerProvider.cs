using Microsoft.Extensions.Logging;

namespace REBUSS.Pure.Logging;

/// <summary>
/// A minimal <see cref="ILoggerProvider"/> that appends log entries to a single file.
/// Used to capture server-side diagnostics (including the MCP <c>initialize</c> request payload)
/// from clients such as Visual Studio Professional that do not expose the server's stderr stream.
/// Log location: <c>%LOCALAPPDATA%\REBUSS.Pure\server.log</c>
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    public FileLoggerProvider(string filePath)
    {
        var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, leaveOpen: false) { AutoFlush = true };
    }

    public ILogger CreateLogger(string categoryName) =>
        new FileLogger(categoryName, _writer, _lock);

    public void Dispose() => _writer.Dispose();
}

internal sealed class FileLogger : ILogger
{
    private readonly string _categoryName;
    private readonly StreamWriter _writer;
    private readonly object _lock;

    internal FileLogger(string categoryName, StreamWriter writer, object @lock)
    {
        _categoryName = categoryName;
        _writer = writer;
        _lock = @lock;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var message = formatter(state, exception);
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel,-11}] {_categoryName}: {message}";

        if (exception is not null)
            line += Environment.NewLine + exception;

        lock (_lock)
        {
            _writer.WriteLine(line);
        }
    }
}
