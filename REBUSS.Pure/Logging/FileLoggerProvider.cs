using Microsoft.Extensions.Logging;

namespace REBUSS.Pure.Logging;

/// <summary>
/// An <see cref="ILoggerProvider"/> that writes log entries to a daily-rotated file
/// and automatically deletes log files older than <see cref="RetainDays"/> days.
/// <para>
/// File naming pattern: <c>&lt;logDirectory&gt;\server-yyyy-MM-dd.log</c><br/>
/// A new file is opened on the first write after midnight.
/// Old files are pruned on startup and on each daily roll-over.
/// </para>
/// <para>
/// Log location: <c>%LOCALAPPDATA%\REBUSS.Pure\server-yyyy-MM-dd.log</c>
/// </para>
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    internal const int RetainDays = 3;
    internal const string FilePrefix = "server-";
    internal const string FileSuffix = ".log";

    private readonly string _logDirectory;
    private readonly Func<DateTime> _nowFactory;

    internal DateTime Now => _nowFactory();
    private readonly object _lock = new();

    private StreamWriter? _writer;
    private DateTime _currentDate;

    /// <param name="logDirectory">
    /// Directory where log files are written (e.g. <c>%LOCALAPPDATA%\REBUSS.Pure</c>).
    /// </param>
    /// <param name="nowFactory">
    /// Optional factory for the current date/time; defaults to <see cref="DateTime.Now"/>.
    /// Provided for testability.
    /// </param>
    public FileLoggerProvider(string logDirectory, Func<DateTime>? nowFactory = null)
    {
        _logDirectory = logDirectory;
        _nowFactory = nowFactory ?? (() => DateTime.Now);

        var today = _nowFactory().Date;
        _currentDate = today;
        _writer = OpenWriter(today);
        DeleteOldLogs(today);
    }

    public ILogger CreateLogger(string categoryName) =>
        new FileLogger(categoryName, this);

    /// <summary>
    /// Writes a pre-formatted log line, rolling to a new file when the calendar date changes.
    /// </summary>
    internal void WriteLine(string line)
    {
        lock (_lock)
        {
            var today = _nowFactory().Date;

            if (today != _currentDate)
            {
                _writer?.Dispose();
                _currentDate = today;
                _writer = OpenWriter(today);
                DeleteOldLogs(today);
            }

            _writer?.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    // --- Helpers -----------------------------------------------------------------

    internal string LogFilePath(DateTime date) =>
        Path.Combine(_logDirectory, $"{FilePrefix}{date:yyyy-MM-dd}{FileSuffix}");

    private StreamWriter OpenWriter(DateTime date)
    {
        var path = LogFilePath(date);
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Write);
        return new StreamWriter(stream, leaveOpen: false) { AutoFlush = true };
    }

    private void DeleteOldLogs(DateTime today)
    {
        try
        {
            var cutoff = today.AddDays(-RetainDays);
            foreach (var file in Directory.EnumerateFiles(_logDirectory, $"{FilePrefix}*{FileSuffix}"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var datePart = name[FilePrefix.Length..];
                if (DateTime.TryParseExact(datePart, "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,
                        out var fileDate)
                    && fileDate < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // Deletion is best-effort — never crash the server over a stale log file.
        }
    }
}

internal sealed class FileLogger : ILogger
{
    private readonly string _categoryName;
    private readonly FileLoggerProvider _provider;

    internal FileLogger(string categoryName, FileLoggerProvider provider)
    {
        _categoryName = categoryName;
        _provider = provider;
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
        var line = $"{_provider.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel,-11}] {_categoryName}: {message}";

        if (exception is not null)
            line += Environment.NewLine + exception;

        _provider.WriteLine(line);
    }
}
