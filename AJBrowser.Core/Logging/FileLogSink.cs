using System.Globalization;
using System.Text;

namespace AJBrowser.Core.Logging;

/// <summary>
/// Thread-safe file logger. Writes one file per day (ajbrowser-yyyyMMdd.log), rolls to
/// ajbrowser-yyyyMMdd.1.log, .2.log ... when a file exceeds <see cref="MaxFileBytes"/>, and keeps
/// at most <see cref="RetainedFiles"/> log files in the directory.
/// </summary>
public sealed class FileLogSink : ILogSink, IDisposable
{
    public const string FilePrefix = "ajbrowser-";
    public const string FileExtension = ".log";

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly TimeProvider _time;
    private StreamWriter? _writer;
    private string? _currentDate;
    private int _currentIndex;
    private bool _disposed;

    public FileLogSink(string directory, TimeProvider? time = null, long maxFileBytes = 5 * 1024 * 1024, int retainedFiles = 10)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFileBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(retainedFiles, 1);

        _directory = directory;
        _time = time ?? TimeProvider.System;
        MaxFileBytes = maxFileBytes;
        RetainedFiles = retainedFiles;

        Directory.CreateDirectory(_directory);
        DeleteOldFiles();
    }

    public long MaxFileBytes { get; }
    public int RetainedFiles { get; }
    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    /// <summary>Path of the file currently being written, if any.</summary>
    public string? CurrentFilePath { get; private set; }

    public void Write(LogEntry entry)
    {
        if (entry.Level < MinimumLevel)
        {
            return;
        }

        var line = Format(entry);
        System.Diagnostics.Debug.Write(line);

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var writer = GetWriter(Encoding.UTF8.GetByteCount(line));
                writer.Write(line);
                writer.Flush();
            }
            catch (IOException)
            {
                // Disk full, file locked, etc. Never let logging crash the app.
                CloseWriter();
            }
            catch (UnauthorizedAccessException)
            {
                CloseWriter();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            CloseWriter();
        }
    }

    internal static string Format(LogEntry entry)
    {
        var level = entry.Level switch
        {
            LogLevel.Debug => "DBG",
            LogLevel.Info => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            _ => "???",
        };

        var builder = new StringBuilder()
            .Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(" [").Append(level).Append("] ")
            .Append(entry.Category).Append(": ")
            .Append(entry.Message)
            .AppendLine();

        if (!string.IsNullOrEmpty(entry.ExceptionText))
        {
            foreach (var exceptionLine in entry.ExceptionText.Split('\n'))
            {
                builder.Append("    ").AppendLine(exceptionLine.TrimEnd('\r'));
            }
        }

        return builder.ToString();
    }

    private StreamWriter GetWriter(int incomingBytes)
    {
        var today = _time.GetLocalNow().ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        if (_writer is not null && _currentDate == today && _writer.BaseStream.Length + incomingBytes <= MaxFileBytes)
        {
            return _writer;
        }

        if (_writer is not null && _currentDate == today)
        {
            // Same day, but the file is full: move to the next index.
            _currentIndex++;
        }
        else
        {
            _currentDate = today;
            _currentIndex = 0;
        }

        CloseWriter();

        // Skip past files from an earlier run of the app that are already full.
        string path;
        while (true)
        {
            path = BuildPath(_currentDate!, _currentIndex);
            var existing = new FileInfo(path);
            if (!existing.Exists || existing.Length + incomingBytes <= MaxFileBytes)
            {
                break;
            }

            _currentIndex++;
        }

        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        CurrentFilePath = path;
        DeleteOldFiles();
        return _writer;
    }

    private string BuildPath(string date, int index) =>
        Path.Combine(_directory, index == 0
            ? $"{FilePrefix}{date}{FileExtension}"
            : $"{FilePrefix}{date}.{index}{FileExtension}");

    private void CloseWriter()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (IOException)
        {
        }

        _writer = null;
    }

    private void DeleteOldFiles()
    {
        try
        {
            var files = new DirectoryInfo(_directory)
                .GetFiles($"{FilePrefix}*{FileExtension}")
                .Where(f => !string.Equals(f.FullName, CurrentFilePath, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            // Keep RetainedFiles in total, counting the file currently open.
            var keep = CurrentFilePath is null ? RetainedFiles : RetainedFiles - 1;
            foreach (var file in files.Skip(Math.Max(keep, 0)))
            {
                file.Delete();
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
