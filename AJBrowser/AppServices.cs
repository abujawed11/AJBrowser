using System.Windows;
using AJBrowser.Browser;
using AJBrowser.Core.Infrastructure;
using AJBrowser.Core.Logging;
using AJBrowser.Core.Navigation;
using AJBrowser.Services;
using AJBrowser.ViewModels;

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
        Browser = new BrowserEnvironment(paths.WebView2UserData, CreateLogger(nameof(BrowserEnvironment)));
        AddressResolver = new UrlOrSearchResolver(BrowserDefaults.SearchUrlTemplate);
    }

    public AppPaths Paths { get; }
    public FileLogSink LogSink { get; }
    public ThemeService Theme { get; }
    public ShellService Shell { get; }

    /// <summary>The shared WebView2 environment (created lazily when the first tab starts).</summary>
    public BrowserEnvironment Browser { get; }

    public UrlOrSearchResolver AddressResolver { get; }

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

    /// <summary>Creates a tab (WebView wrapper + its view model). Not started yet: call <see cref="TabViewModel.StartAsync"/>.</summary>
    public TabViewModel CreateTab() =>
        new(new BrowserTab(Browser, Theme, CreateLogger(nameof(BrowserTab))),
            AddressResolver,
            BrowserDefaults.HomePage,
            CreateLogger(nameof(TabViewModel)));

    public void Dispose()
    {
        Theme.Dispose();
        LogSink.Dispose();
    }
}
