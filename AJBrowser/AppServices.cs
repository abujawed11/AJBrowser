using System.Windows;
using AJBrowser.Core.Infrastructure;
using AJBrowser.Core.Logging;
using AJBrowser.Services;

namespace AJBrowser;

/// <summary>
/// Composition root: creates the app-wide services once at startup and hands them to whoever needs them.
/// Deliberately hand-written (no DI container) so it is easy to follow. Add new services here.
/// </summary>
public sealed class AppServices : IDisposable
{
    private AppServices(AppPaths paths, FileLogSink logSink, ThemeService theme, ShellService shell)
    {
        Paths = paths;
        LogSink = logSink;
        Theme = theme;
        Shell = shell;
    }

    public AppPaths Paths { get; }
    public FileLogSink LogSink { get; }
    public ThemeService Theme { get; }
    public ShellService Shell { get; }

    public static AppServices Create(Application app)
    {
        var paths = AppPaths.CreateDefault();
        paths.EnsureCreated();

        var logSink = new FileLogSink(paths.Logs)
        {
#if DEBUG
            MinimumLevel = LogLevel.Debug,
#endif
        };

        var theme = new ThemeService(app);
        theme.Apply(AppTheme.System); // Persisted theme choice arrives with settings in Phase 3.

        return new AppServices(paths, logSink, theme, new ShellService());
    }

    public AppLogger CreateLogger(string category) => new(LogSink, category);

    public void Dispose()
    {
        Theme.Dispose();
        LogSink.Dispose();
    }
}
