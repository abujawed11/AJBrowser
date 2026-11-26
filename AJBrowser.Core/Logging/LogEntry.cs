namespace AJBrowser.Core.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>One log record. Message and exception text are already redacted when created by <see cref="AppLogger"/>.</summary>
public sealed record LogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Category,
    string Message,
    string? ExceptionText = null);

/// <summary>Destination for log entries. Implementations must never throw.</summary>
public interface ILogSink
{
    void Write(LogEntry entry);
}
