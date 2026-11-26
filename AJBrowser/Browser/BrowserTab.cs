using System.Windows;
using System.Windows.Controls;
using AJBrowser.Core.Logging;
using AJBrowser.Core.Security;
using AJBrowser.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AJBrowser.Browser;

/// <summary>
/// One browser page: wraps a WebView2 control and turns its events into plain C# state and events.
/// This (with <see cref="BrowserEnvironment"/>) is the only code that touches CoreWebView2; view models
/// use the properties, methods and events here instead.
/// <para>
/// The WebView2 control lives inside <see cref="View"/>, a container that stays the same even when the
/// control itself is replaced after a browser-process crash.
/// </para>
/// </summary>
public sealed class BrowserTab : IDisposable
{
    /// <summary>Recovering more often than this means something is badly wrong; ask the user instead of looping.</summary>
    private static readonly TimeSpan MinimumTimeBetweenAutoRecoveries = TimeSpan.FromSeconds(30);

    private readonly BrowserEnvironment _environment;
    private readonly ThemeService _theme;
    private readonly AppLogger _log;
    private readonly Grid _host = new();

    private WebView2? _webView;
    private CoreWebView2? _core;
    private CoreWebView2Environment? _attachedEnvironment;
    private Uri? _pendingNavigation;
    private ulong _currentNavigationId;
    private Uri? _currentNavigationUri;
    private DateTime _lastAutoRecoveryUtc = DateTime.MinValue;
    private bool _disposed;

    public BrowserTab(BrowserEnvironment environment, ThemeService theme, AppLogger log)
    {
        _environment = environment;
        _theme = theme;
        _log = log;
        _theme.ThemeChanged += OnThemeChanged;
    }

    /// <summary>The element to place in the window. Its child WebView2 control may be swapped during recovery.</summary>
    public FrameworkElement View => _host;

    public bool IsReady => _core is not null;
    public string? Url { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public bool IsLoading { get; private set; }
    public bool CanGoBack { get; private set; }
    public bool CanGoForward { get; private set; }

    /// <summary>Raised when any of the state properties above changed.</summary>
    public event EventHandler? StateChanged;

    /// <summary>A new top-level navigation began (any earlier problem panel is now stale).</summary>
    public event EventHandler? NavigationStarted;

    /// <summary>Something the user must see instead of the page (error, crash, engine failure).</summary>
    public event EventHandler<PageProblem>? ProblemOccurred;

    /// <summary>Creates the WebView2 control. Problems are reported through <see cref="ProblemOccurred"/>, never thrown.</summary>
    public Task InitializeAsync() => StartEngineAsync(recreateEnvironment: false);

    /// <summary>Throws away the WebView2 control and starts again with a fresh environment, then reloads the last page.</summary>
    public Task RestartEngineAsync() => StartEngineAsync(recreateEnvironment: true);

    /// <summary>Navigates now, or as soon as the WebView is ready.</summary>
    public void Navigate(Uri uri)
    {
        if (_core is null)
        {
            _pendingNavigation = uri;
            return;
        }

        try
        {
            _core.Navigate(uri.AbsoluteUri);
        }
        catch (ArgumentException ex)
        {
            _log.Warn($"Navigate rejected {Redactor.Url(uri)}", ex);
        }
    }

    public void GoBack()
    {
        if (_core?.CanGoBack == true)
        {
            _core.GoBack();
        }
    }

    public void GoForward()
    {
        if (_core?.CanGoForward == true)
        {
            _core.GoForward();
        }
    }

    public void Reload() => _core?.Reload();

    public void Stop() => _core?.Stop();

    /// <summary>Moves keyboard focus into the page (for example after pressing Enter in the address bar).</summary>
    public void FocusPage() => _webView?.Focus();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _theme.ThemeChanged -= OnThemeChanged;
        DetachWebView();
    }

    // ---------------------------------------------------------------- creating the WebView

    private async Task StartEngineAsync(bool recreateEnvironment)
    {
        if (_disposed)
        {
            return;
        }

        // After a restart, go back to the page we were on.
        if (_pendingNavigation is null && Url is not null && Uri.TryCreate(Url, UriKind.Absolute, out var last))
        {
            _pendingNavigation = last;
        }

        var failedEnvironment = _attachedEnvironment;
        DetachWebView();

        try
        {
            var environment = recreateEnvironment
                ? await _environment.RecreateAsync(failedEnvironment)
                : await _environment.GetAsync();
            await AttachNewWebViewAsync(environment);
        }
        catch (Exception ex)
        {
            _log.Error("Could not start the WebView2 engine", ex);
            ProblemOccurred?.Invoke(this, PageProblem.EngineFailed(Redactor.Text(ex.Message)));
        }
    }

    private async Task AttachNewWebViewAsync(CoreWebView2Environment environment)
    {
        var webView = new WebView2 { DefaultBackgroundColor = BackgroundFor(_theme.IsDark) };
        _host.Children.Add(webView);
        _webView = webView;

        try
        {
            // Completes once the control has a window handle, i.e. once it is visible in the window.
            await webView.EnsureCoreWebView2Async(environment, _environment.CreateControllerOptions(environment));
        }
        catch
        {
            if (ReferenceEquals(_webView, webView))
            {
                DetachWebView();
            }

            throw;
        }

        if (_disposed || !ReferenceEquals(_webView, webView))
        {
            return; // Disposed or replaced while we were waiting.
        }

        _attachedEnvironment = environment;
        _core = webView.CoreWebView2;
        Configure(_core);

        if (_pendingNavigation is { } target)
        {
            _pendingNavigation = null;
            Navigate(target);
        }
    }

    /// <summary>
    /// ALL per-WebView setup goes here, and only here. WebViews created for popups (Phase 2) or after a
    /// crash don't inherit anything from other WebViews (spike F10), so every WebView must pass through this.
    /// </summary>
    private void Configure(CoreWebView2 core)
    {
        var settings = core.Settings;
#if !DEBUG
        settings.AreDevToolsEnabled = false; // F12 DevTools stay available in Debug builds.
#endif
        settings.IsStatusBarEnabled = true;

        core.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Balanced; // Strict becomes a setting later (spike F8).
        ApplyColorScheme(core);

        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.SourceChanged += OnSourceChanged;
        core.HistoryChanged += OnHistoryChanged;
        core.DocumentTitleChanged += OnDocumentTitleChanged;
        core.ProcessFailed += OnProcessFailed;
        core.NewWindowRequested += OnNewWindowRequested;

        UpdateState(core);
    }

    private void DetachWebView()
    {
        if (_core is not null)
        {
            try
            {
                _core.NavigationStarting -= OnNavigationStarting;
                _core.NavigationCompleted -= OnNavigationCompleted;
                _core.SourceChanged -= OnSourceChanged;
                _core.HistoryChanged -= OnHistoryChanged;
                _core.DocumentTitleChanged -= OnDocumentTitleChanged;
                _core.ProcessFailed -= OnProcessFailed;
                _core.NewWindowRequested -= OnNewWindowRequested;
            }
            catch (Exception ex)
            {
                // Expected after the browser process died: the COM object is gone.
                _log.Debug($"Unsubscribing from a dead WebView: {ex.GetType().Name}");
            }

            _core = null;
        }

        if (_webView is not null)
        {
            _host.Children.Remove(_webView);
            try
            {
                _webView.Dispose();
            }
            catch (Exception ex)
            {
                _log.Debug($"Disposing a dead WebView: {ex.GetType().Name}");
            }

            _webView = null;
        }

        _attachedEnvironment = null;
        if (IsLoading)
        {
            IsLoading = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---------------------------------------------------------------- WebView2 events

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        _currentNavigationId = e.NavigationId;
        _currentNavigationUri = Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) ? uri : null;
        IsLoading = true;
        NavigationStarted?.Invoke(this, EventArgs.Empty);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        // A newer navigation may already be running; only the latest one decides the loading state.
        if (e.NavigationId != _currentNavigationId)
        {
            return;
        }

        IsLoading = false;
        if (sender is CoreWebView2 core)
        {
            UpdateState(core);
        }

        if (!e.IsSuccess)
        {
            _log.Info($"Navigation failed: {e.WebErrorStatus}, HTTP {e.HttpStatusCode}, {Redactor.Url(_currentNavigationUri)}");

            if (NavigationErrors.ShouldShowErrorPanel(e.WebErrorStatus, e.HttpStatusCode))
            {
                ProblemOccurred?.Invoke(this, NavigationErrors.ToProblem(e.WebErrorStatus, _currentNavigationUri));
            }
        }
    }

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e) => UpdateState((CoreWebView2)sender!);

    private void OnHistoryChanged(object? sender, object e) => UpdateState((CoreWebView2)sender!);

    private void OnDocumentTitleChanged(object? sender, object e) => UpdateState((CoreWebView2)sender!);

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _log.Warn($"WebView2 process failed: {e.ProcessFailedKind}, reason {e.Reason}, exit code {e.ExitCode}, {e.ProcessDescription}");

        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
                ProblemOccurred?.Invoke(this, PageProblem.RendererCrashed());
                break;

            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                ProblemOccurred?.Invoke(this, PageProblem.RendererUnresponsive());
                break;

            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                // The whole engine is gone and this WebView can't be reused. Must not run inside this event
                // handler (we're about to dispose the WebView raising it), so continue on the next dispatcher turn.
                _host.Dispatcher.BeginInvoke(RecoverFromBrowserExit);
                break;

            default:
                // GPU, utility and iframe renderer processes: WebView2 recovers from these by itself.
                break;
        }
    }

    private void RecoverFromBrowserExit()
    {
        if (_disposed)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now - _lastAutoRecoveryUtc < MinimumTimeBetweenAutoRecoveries)
        {
            DetachWebView();
            ProblemOccurred?.Invoke(this, PageProblem.EngineFailed("The engine stopped again shortly after restarting."));
            return;
        }

        _lastAutoRecoveryUtc = now;
        _log.Info("Recreating the WebView2 environment after the browser process exited");
        _ = RestartEngineAsync();
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // Never let WebView2 open its own window: it would skip Configure() (spike F10).
        // Phase 1 has no tabs, so links that open a new window load in this page instead, and script
        // popups the user didn't click are blocked (WebView2 doesn't block them; spike F10/F14).
        // Phase 2 replaces this with real tabs and window.opener support.
        e.Handled = true;

        if (e.IsUserInitiated
            && Uri.TryCreate(e.Uri, UriKind.Absolute, out var target)
            && (target.Scheme == Uri.UriSchemeHttps || target.Scheme == Uri.UriSchemeHttp))
        {
            Navigate(target);
        }
        else
        {
            _log.Info($"Blocked a new window (user-initiated: {e.IsUserInitiated}) to {Redactor.Url(e.Uri)}");
        }
    }

    // ---------------------------------------------------------------- state and theme

    private void UpdateState(CoreWebView2 core)
    {
        Url = core.Source;
        Title = core.DocumentTitle ?? string.Empty;
        CanGoBack = core.CanGoBack;
        CanGoForward = core.CanGoForward;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (_webView is not null)
        {
            _webView.DefaultBackgroundColor = BackgroundFor(_theme.IsDark);
        }

        if (_core is not null)
        {
            ApplyColorScheme(_core);
        }
    }

    /// <summary>Pages that support dark mode (prefers-color-scheme) follow the app theme.</summary>
    private void ApplyColorScheme(CoreWebView2 core) =>
        core.Profile.PreferredColorScheme = _theme.Theme switch
        {
            AppTheme.Light => CoreWebView2PreferredColorScheme.Light,
            AppTheme.Dark => CoreWebView2PreferredColorScheme.Dark,
            _ => CoreWebView2PreferredColorScheme.Auto, // follows Windows, same as our System theme
        };

    /// <summary>Shown before a page paints; matches ContentBackgroundBrush to avoid a white flash in dark mode.</summary>
    private static System.Drawing.Color BackgroundFor(bool dark) =>
        dark ? System.Drawing.Color.FromArgb(0x1C, 0x1C, 0x1C) : System.Drawing.Color.White;
}
