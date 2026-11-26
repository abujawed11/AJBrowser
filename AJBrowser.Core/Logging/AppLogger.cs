using AJBrowser.Core.Security;

namespace AJBrowser.Core.Logging;

/// <summary>
/// Category-scoped logger. All text is passed through <see cref="Redactor.Text"/> before it reaches the sink,
/// so call sites cannot accidentally log cookies, tokens or signed URLs.
/// </summary>
public sealed class AppLogger
{
    private readonly ILogSink _sink;
    private readonly TimeProvider _time;

    public AppLogger(ILogSink sink, string category, TimeProvider? time = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        Category = category ?? throw new ArgumentNullException(nameof(category));
        _time = time ?? TimeProvider.System;
    }

    public string Category { get; }

    public void Debug(string message) => Write(LogLevel.Debug, message, null);

    public void Info(string message) => Write(LogLevel.Info, message, null);

    public void Warn(string message, Exception? exception = null) => Write(LogLevel.Warning, message, exception);

    public void Error(string message, Exception? exception = null) => Write(LogLevel.Error, message, exception);

    private void Write(LogLevel level, string message, Exception? exception)
    {
        try
        {
            _sink.Write(new LogEntry(
                _time.GetLocalNow(),
                level,
                Category,
                Redactor.Text(message),
                exception is null ? null : Redactor.Text(exception.ToString())));
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
