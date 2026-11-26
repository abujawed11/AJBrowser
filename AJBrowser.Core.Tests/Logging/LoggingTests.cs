using AJBrowser.Core.Logging;

namespace AJBrowser.Core.Tests.Logging;

internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

internal sealed class ListSink : ILogSink
{
    public List<LogEntry> Entries { get; } = [];

    public void Write(LogEntry entry) => Entries.Add(entry);
}

public class AppLoggerTests
{
    [Fact]
    public void Messages_and_exceptions_are_redacted_before_reaching_the_sink()
    {
        var sink = new ListSink();
        var logger = new AppLogger(sink, "Downloads");

        logger.Error(
            "Failed https://cdn.example.com/v.mp4?sig=SECRET with Cookie: sid=SECRET",
            new InvalidOperationException("server said https://x.example/a?token=SECRET"));

        var entry = Assert.Single(sink.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal("Downloads", entry.Category);
        Assert.DoesNotContain("SECRET", entry.Message);
        Assert.NotNull(entry.ExceptionText);
        Assert.DoesNotContain("SECRET", entry.ExceptionText);
        Assert.Contains("InvalidOperationException", entry.ExceptionText);
    }

    [Fact]
    public void A_throwing_sink_does_not_propagate()
    {
        var logger = new AppLogger(new ThrowingSink(), "Test");

        logger.Info("hello"); // must not throw
    }

    private sealed class ThrowingSink : ILogSink
    {
        public void Write(LogEntry entry) => throw new IOException("boom");
    }
}

public sealed class FileLogSinkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AJBrowserTests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Writes_formatted_lines_to_a_daily_file()
    {
        using (var sink = new FileLogSink(_dir, _time))
        {
            new AppLogger(sink, "App", _time).Info("started");
        }

        var file = Path.Combine(_dir, "ajbrowser-20261126.log");
        Assert.True(File.Exists(file));
        Assert.Equal("2026-11-26 10:00:00.000 +00:00 [INF] App: started" + Environment.NewLine, File.ReadAllText(file));
    }

    [Fact]
    public void Entries_below_minimum_level_are_skipped()
    {
        using (var sink = new FileLogSink(_dir, _time) { MinimumLevel = LogLevel.Warning })
        {
            var logger = new AppLogger(sink, "App", _time);
            logger.Info("ignored");
            logger.Warn("kept");
        }

        var text = File.ReadAllText(Path.Combine(_dir, "ajbrowser-20261126.log"));
        Assert.DoesNotContain("ignored", text);
        Assert.Contains("kept", text);
    }

    [Fact]
    public void Rolls_to_a_new_index_when_the_file_is_full()
    {
        using (var sink = new FileLogSink(_dir, _time, maxFileBytes: 1024))
        {
            var logger = new AppLogger(sink, "App", _time);
            for (var i = 0; i < 40; i++)
            {
                logger.Info($"line {i} " + new string('x', 40));
            }
        }

        Assert.True(File.Exists(Path.Combine(_dir, "ajbrowser-20261126.log")));
        Assert.True(File.Exists(Path.Combine(_dir, "ajbrowser-20261126.1.log")));
        Assert.All(Directory.GetFiles(_dir), f => Assert.True(new FileInfo(f).Length <= 1024));
    }

    [Fact]
    public void Starts_a_new_file_when_the_day_changes()
    {
        using (var sink = new FileLogSink(_dir, _time))
        {
            var logger = new AppLogger(sink, "App", _time);
            logger.Info("day one");
            _time.Now = _time.Now.AddDays(1);
            logger.Info("day two");
        }

        Assert.Contains("day one", File.ReadAllText(Path.Combine(_dir, "ajbrowser-20261126.log")));
        Assert.Contains("day two", File.ReadAllText(Path.Combine(_dir, "ajbrowser-20261127.log")));
    }

    [Fact]
    public void Keeps_only_the_retained_number_of_files()
    {
        Directory.CreateDirectory(_dir);
        for (var day = 1; day <= 5; day++)
        {
            var old = Path.Combine(_dir, $"ajbrowser-202611{day:00}.log");
            File.WriteAllText(old, "old");
            File.SetLastWriteTimeUtc(old, new DateTime(2026, 11, day, 0, 0, 0, DateTimeKind.Utc));
        }

        using (var sink = new FileLogSink(_dir, _time, retainedFiles: 3))
        {
            new AppLogger(sink, "App", _time).Info("today");
        }

        var remaining = Directory.GetFiles(_dir).Select(f => Path.GetFileName(f)).Order().ToArray();
        string[] expected = ["ajbrowser-20261104.log", "ajbrowser-20261105.log", "ajbrowser-20261126.log"];
        Assert.Equal(expected, remaining);
    }

    [Fact]
    public void Exception_text_is_indented_under_the_message()
    {
        var entry = new LogEntry(_time.Now, LogLevel.Error, "App", "failed", "System.Exception: boom\r\n   at Foo()");

        var formatted = FileLogSink.Format(entry);

        Assert.Contains("[ERR] App: failed", formatted);
        Assert.Contains("    System.Exception: boom", formatted);
        Assert.Contains("       at Foo()", formatted);
    }
}
