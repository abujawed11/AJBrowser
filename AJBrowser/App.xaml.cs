using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using AJBrowser.Core.Logging;
using AJBrowser.Core.Security;
using AJBrowser.ViewModels;
using AJBrowser.Views;

namespace AJBrowser;

public partial class App : Application
{
    private AppServices? _services;
    private AppLogger? _log;
    private bool _isShowingErrorDialog;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RegisterGlobalExceptionHandlers();

        try
        {
            _services = AppServices.Create(this);
            _log = _services.CreateLogger("App");
            _log.Info($"AJBrowser {GetVersion()} starting. .NET {Environment.Version}, {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}");

            var viewModel = new MainWindowViewModel(
                _services.Paths,
                _services.Theme,
                _services.Shell,
                _services.CreateLogger(nameof(MainWindowViewModel)));

            var window = new MainWindow(viewModel);
            MainWindow = window;
            window.Show();

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

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.Info($"AJBrowser exiting with code {e.ApplicationExitCode}");
        _services?.Dispose();
        base.OnExit(e);
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
