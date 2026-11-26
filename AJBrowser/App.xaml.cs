using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using AJBrowser.Browser;
using AJBrowser.Core.Logging;
using AJBrowser.Core.Navigation;
using AJBrowser.Core.Security;
using AJBrowser.ViewModels;
using AJBrowser.Views;

namespace AJBrowser;

public partial class App : Application
{
    private AppServices? _services;
    private AppLogger? _log;
    private MainWindowViewModel? _mainViewModel;
    private bool _isShowingErrorDialog;

    /// <summary>
    /// async void is the one place it's normal in WPF: OnStartup is an event-style override. Everything up to the
    /// first await runs before any window shows; failures are caught below.
    /// </summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RegisterGlobalExceptionHandlers();

        try
        {
            _services = AppServices.Create(this);
            _log = _services.CreateLogger("App");
            _log.Info($"AJBrowser {GetVersion()} starting. .NET {Environment.Version}, {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}");

            if (!CheckWebView2Runtime(_services))
            {
                Shutdown(2);
                return;
            }

            _mainViewModel = new MainWindowViewModel(
                _services.Paths,
                _services.Settings,
                _services.Shell,
                _services.Extensions,
                _services.Bookmarks,
                _services.Favicons,
                _services.Session,
                _services.Downloads,
                _services.CreateTab,
                _services.CreateLogger(nameof(MainWindowViewModel)));
            _services.AdBlock = _mainViewModel.AdBlock;
            var mainViewModel = _mainViewModel;
            var mediaDownloads = _services.MediaDownloads;
            _services.Downloads.RefreshSourceBeforeRetry = item => mediaDownloads.RefreshSourceAsync(item, mainViewModel.FindMediaInTabs);

            var window = new MainWindow(_mainViewModel);
            MainWindow = window;
            window.Show();

            if (_services.DatabaseResult.RecreatedAfterCorruption)
            {
                MessageBox.Show(
                    "AJBrowser's history and bookmarks file was damaged, so a new empty one was created.\n\n" +
                    $"The damaged file was kept here:\n{_services.DatabaseResult.CorruptBackupPath}",
                    "AJBrowser",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            // Bookmarks (for the bar and the star) load in the background while the tabs open.
            var bookmarksLoaded = Task.WhenAll(_services.Favicons.LoadAsync(), _services.Bookmarks.LoadAsync());
            var downloadsLoaded = LoadDownloadsAsync(_services);

            // The saved session (if "Continue where you left off" is on), then URLs or files from the command line;
            // otherwise one tab with the home page. Starting tabs never throws: problems show in each tab's page area.
            await _mainViewModel.OpenStartupTabsAsync(CommandLineUrls.Parse(e.Args, _services.AddressResolver, System.IO.File.Exists));
            await bookmarksLoaded;
            await downloadsLoaded;

#if DEBUG
            // Phase 0.5 spike: "--diagnostics" opens the Diagnostics window. "--diagnostics-auto" also runs the
            // automated suite, writes a report and exits; "--diagnostics-auto=media,popup" runs only those tests.
            var autoArg = e.Args.FirstOrDefault(a => a.StartsWith("--diagnostics-auto", StringComparison.Ordinal));
            if (autoArg is not null)
            {
                var tests = autoArg.Contains('=')
                    ? autoArg[(autoArg.IndexOf('=') + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : [];
                OpenDiagnostics(new HashSet<string>(tests, StringComparer.OrdinalIgnoreCase));
            }
            else if (e.Args.Contains("--diagnostics"))
            {
                OpenDiagnostics(null);
            }
#endif
        }
        catch (Exception ex)
        {
            _log?.Error("Startup failed", ex);
            MessageBox.Show(
                $"AJBrowser could not start.\n\n{Redactor.Text(ex.Message)}",
                "AJBrowser",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>The download list (downloads that were running when AJBrowser closed are marked Interrupted/Paused first).</summary>
    private async Task LoadDownloadsAsync(AppServices services)
    {
        try
        {
            await services.DownloadManager.InitializeAsync();
            services.Downloads.Load();
        }
        catch (Exception ex)
        {
            _log?.Error("Loading the download list failed", ex);
            return;
        }

        await OfferPartialFileCleanupAsync(services);
        RemoveStaleStreamTemp(services);
    }

    /// <summary>
    /// Temporary pieces of stream downloads (Temp\hls\{id}) are AJBrowser's own files: keep those of stream jobs that
    /// can still continue, delete the rest (finished, cancelled or removed from the list) without asking.
    /// </summary>
    private void RemoveStaleStreamTemp(AppServices services)
    {
        try
        {
            var root = System.IO.Path.Combine(services.Paths.Temp, "hls");
            if (!System.IO.Directory.Exists(root))
            {
                return;
            }

            var keep = services.DownloadManager.Items
                .Where(i => i.Source == Core.Downloads.DownloadSource.Hls && i.State is not (Core.Downloads.DownloadState.Completed or Core.Downloads.DownloadState.Cancelled))
                .Select(i => i.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ToHashSet();
            foreach (var folder in System.IO.Directory.GetDirectories(root))
            {
                if (!keep.Contains(System.IO.Path.GetFileName(folder)))
                {
                    services.Hls.DeleteTemp(long.TryParse(System.IO.Path.GetFileName(folder), out var id) ? id : -1);
                    if (System.IO.Directory.Exists(folder))
                    {
                        System.IO.Directory.Delete(folder, recursive: true);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Debug($"Stream temp cleanup skipped: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Old ".ajpart" files (unfinished AJBrowser downloads that nothing will continue) in the download folders:
    /// offer to delete them. Never deletes without asking (plan Phase 4c).
    /// </summary>
    private async Task OfferPartialFileCleanupAsync(AppServices services)
    {
        try
        {
            var folders = new[] { services.Settings.Current.DownloadFolder ?? string.Empty, Services.KnownFolders.Downloads };
            var items = services.DownloadManager.Items;
            var stale = await Task.Run(() => Core.Downloads.PartialFileCleanup.Find(folders, items, Core.Downloads.PartialFileCleanup.DefaultAge, DateTime.UtcNow));
            if (stale.Count == 0)
            {
                return;
            }

            var size = Core.Downloads.DownloadText.Size(stale.Sum(f => f.Bytes));
            var answer = MessageBox.Show(
                MainWindow,
                $"AJBrowser found {stale.Count} unfinished download file(s) older than {Core.Downloads.PartialFileCleanup.DefaultAge.Days} days ({size}, ending in \".ajpart\") that no download will continue.\n\nDelete them?",
                "AJBrowser",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            if (answer == MessageBoxResult.Yes)
            {
                var deleted = await Task.Run(() => Core.Downloads.PartialFileCleanup.Delete(stale));
                _log?.Info($"Deleted {deleted} of {stale.Count} old partial download file(s)");
            }
        }
        catch (Exception ex)
        {
            _log?.Warn("Checking for old partial download files failed", ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.Info($"AJBrowser exiting with code {e.ApplicationExitCode}");
        _mainViewModel?.Dispose();

        // App downloads pause (partial files kept for next time) and pending database writes finish. Browser downloads
        // end with the browser process and show as Interrupted next time. Run off the UI thread so nothing deadlocks.
        if (_services is { } services)
        {
            try
            {
                Task.Run(() => services.DownloadManager.ShutdownAsync(TimeSpan.FromSeconds(2))).Wait(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                _log?.Warn("Stopping downloads at exit failed", ex);
            }
        }

        _services?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// AJBrowser can't work without the Microsoft Edge WebView2 Runtime (preinstalled on Windows 11).
    /// If it's missing, explain and offer the official download page instead of crashing.
    /// </summary>
    private bool CheckWebView2Runtime(AppServices services)
    {
        var version = BrowserEnvironment.GetInstalledRuntimeVersion();
        if (version is not null)
        {
            _log?.Info($"WebView2 Runtime {version}");
            return true;
        }

        _log?.Warn("WebView2 Runtime not found; showing the install prompt");
        var answer = MessageBox.Show(
            "AJBrowser needs the Microsoft Edge WebView2 Runtime, which isn't installed on this PC.\n\n" +
            "Open the download page now? Install the \"Evergreen\" runtime, then start AJBrowser again.",
            "AJBrowser",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer == MessageBoxResult.Yes)
        {
            try
            {
                services.Shell.OpenUrl(BrowserEnvironment.RuntimeDownloadUrl);
            }
            catch (Exception ex)
            {
                _log?.Error("Could not open the WebView2 download page", ex);
            }
        }

        return false;
    }

    private void RegisterGlobalExceptionHandlers()
    {
        // Exceptions on the UI thread: log, tell the user, and keep running.
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Faulted tasks nobody awaited: log and mark observed so they never crash the process.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log?.Warn("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        // Exceptions on other threads cannot be recovered; record them before the process ends.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            _log?.Error($"Fatal unhandled exception (terminating: {args.IsTerminating})", args.ExceptionObject as Exception);
            _services?.LogSink.Dispose(); // flush
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;

        if (_isShowingErrorDialog)
        {
            return; // Avoid a cascade of dialogs if the error repeats.
        }

        _isShowingErrorDialog = true;
        try
        {
            var logHint = _services is null ? string.Empty : $"\n\nDetails were written to:\n{_services.Paths.Logs}";
            MessageBox.Show(
                $"Something went wrong, but AJBrowser will try to keep running.\n\n{Redactor.Text(e.Exception.Message)}{logHint}",
                "AJBrowser",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _isShowingErrorDialog = false;
        }
    }

#if DEBUG
    internal void OpenDiagnostics(IReadOnlySet<string>? autoTests = null)
    {
        if (_services is null)
        {
            return;
        }

        var window = new Diagnostics.DiagnosticsWindow(_services.Paths, _services.CreateLogger("Diagnostics"), autoTests);
        window.Show();
    }
#endif

    private static string GetVersion() =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";
}
