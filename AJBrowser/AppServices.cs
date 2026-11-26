using System.IO;
using System.Windows;
using AJBrowser.Browser;
using AJBrowser.Core.Infrastructure;
using AJBrowser.Core.Logging;
using AJBrowser.Core.Navigation;
using AJBrowser.Core.Persistence;
using AJBrowser.Core.Settings;
using AJBrowser.Services;
using AJBrowser.ViewModels;
using AJBrowser.ViewModels.Pages;

namespace AJBrowser;

/// <summary>
/// Composition root: creates the app-wide services once at startup and hands them to whoever needs them.
/// Deliberately hand-written (no DI container) so it is easy to follow. Add new services here.
/// </summary>
public sealed class AppServices : IDisposable
{
    private readonly TabServices _tabServices;

    private AppServices(AppPaths paths, FileLogSink logSink, SettingsStore settings, ThemeService theme, ShellService shell, Database database, DatabaseOpenResult databaseResult)
    {
        Paths = paths;
        LogSink = logSink;
        Settings = settings;
        Theme = theme;
        Shell = shell;
        Database = database;
        DatabaseResult = databaseResult;

        Browser = new BrowserEnvironment(paths.WebView2UserData, CreateLogger(nameof(BrowserEnvironment)));
        Extensions = new ExtensionManager(
            Path.Combine(AppContext.BaseDirectory, "Extensions"),
            paths.Extensions,
            CreateLogger(nameof(ExtensionManager)));
        AddressResolver = new UrlOrSearchResolver(SearchEngines.TemplateFor(settings.Current));
        History = new HistoryService(new HistoryRepository(database), CreateLogger(nameof(HistoryService)));
        Bookmarks = new BookmarkService(new BookmarkRepository(database), CreateLogger(nameof(BookmarkService)));
        Favicons = new FaviconService(new FaviconRepository(database), CreateLogger(nameof(FaviconService)));
        Session = new SessionService(new SessionRepository(database), CreateLogger(nameof(SessionService)));

        _tabServices = new TabServices(
            AddressResolver,
            () => Settings.Current.HomePageUri,
            History,
            Bookmarks,
            Favicons,
            CreatePage,
            CreateLogger(nameof(TabViewModel)));

        Settings.Changed += OnSettingsChanged;
    }

    public AppPaths Paths { get; }
    public FileLogSink LogSink { get; }
    public SettingsStore Settings { get; }
    public ThemeService Theme { get; }
    public ShellService Shell { get; }
    public Database Database { get; }

    /// <summary>How opening the database went (for the "your data was reset" notice).</summary>
    public DatabaseOpenResult DatabaseResult { get; }

    /// <summary>The shared WebView2 environment (created lazily when the first tab starts).</summary>
    public BrowserEnvironment Browser { get; }

    /// <summary>The bundled ad blocker (uBlock Origin Lite), installed when the first tab starts.</summary>
    public ExtensionManager Extensions { get; }

    public UrlOrSearchResolver AddressResolver { get; }
    public HistoryService History { get; }
    public BookmarkService Bookmarks { get; }

    /// <summary>Site icons for History, Bookmarks, the bookmarks bar and suggestions.</summary>
    public FaviconService Favicons { get; }
    public SessionService Session { get; }

    /// <summary>Set by App once the main window's view model exists (the Settings page shows the same switch).</summary>
    public AdBlockViewModel? AdBlock { get; set; }

    /// <summary>
    /// Loads settings and opens the database before any window exists (both are quick local files), so the first
    /// window already has the right theme, search engine and bookmarks.
    /// </summary>
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

        var settings = SettingsStore.Load(paths.SettingsFile);
        if (settings.LoadProblem is { } problem)
        {
            new AppLogger(logSink, "Settings").Warn($"settings.json couldn't be read; using defaults (a copy was kept as settings.json.bad). {problem}");
        }

        var theme = new ThemeService(app);
        theme.Apply(ToAppTheme(settings.Current.Theme));

        var database = new Database(paths.DatabaseFile);
        var databaseResult = database.Initialize();
        var dbLog = new AppLogger(logSink, nameof(Database));
        dbLog.Info($"Database ready (schema v{databaseResult.SchemaVersion})");
        if (databaseResult.RecreatedAfterCorruption)
        {
            dbLog.Warn($"The database was damaged and has been recreated; the old file was kept as {databaseResult.CorruptBackupPath}");
        }

        return new AppServices(paths, logSink, settings, theme, new ShellService(), database, databaseResult);
    }

    public AppLogger CreateLogger(string category) => new(LogSink, category);

    /// <summary>Creates a tab (WebView wrapper + its view model). Not started yet: call <see cref="TabViewModel.StartAsync"/>.</summary>
    public TabViewModel CreateTab() =>
        new(new BrowserTab(Browser, Extensions, Theme, CreateLogger(nameof(BrowserTab))), _tabServices);

    /// <summary>Creates the view model of an internal page for the tab that will show it.</summary>
    private IInternalPage CreatePage(InternalPage page, IPageHost host) => page switch
    {
        InternalPage.History => new HistoryPageViewModel(History, Favicons, Browser, host, CreateLogger("HistoryPage")),
        InternalPage.Bookmarks => new BookmarksPageViewModel(Bookmarks, Favicons, host),
        InternalPage.Settings => new SettingsPageViewModel(
            Settings,
            AdBlock ?? throw new InvalidOperationException("AdBlock must be set before pages are created."),
            Shell,
            host,
            Paths.Root),
        _ => throw new ArgumentOutOfRangeException(nameof(page), page, null),
    };

    /// <summary>Applies settings that services own: the theme and the search engine.</summary>
    private void OnSettingsChanged(object? sender, AppSettings previous)
    {
        var current = Settings.Current;
        if (current.Theme != previous.Theme)
        {
            Theme.Apply(ToAppTheme(current.Theme));
        }

        AddressResolver.SearchTemplate = SearchEngines.TemplateFor(current);
    }

    private static AppTheme ToAppTheme(ThemeSetting theme) => theme switch
    {
        ThemeSetting.Light => AppTheme.Light,
        ThemeSetting.Dark => AppTheme.Dark,
        _ => AppTheme.System,
    };

    public void Dispose()
    {
        Settings.Changed -= OnSettingsChanged;
        Theme.Dispose();
        LogSink.Dispose();
    }
}
