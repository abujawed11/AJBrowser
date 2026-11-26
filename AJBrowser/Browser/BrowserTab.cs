using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AJBrowser.Core.Logging;
using AJBrowser.Core.Navigation;
using AJBrowser.Core.Security;
using AJBrowser.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AJBrowser.Browser;

/// <summary>
/// One browser page: wraps a WebView2 control and turns its events into plain C# state and events.
/// This (with <see cref="BrowserEnvironment"/> and <see cref="PopupRequest"/>) is the only code that touches
/// CoreWebView2; view models use the properties, methods and events here instead.
/// <para>
/// The WebView2 control lives inside <see cref="View"/>, a container that stays the same even when the
/// control itself is replaced after a browser-process crash.
/// </para>
/// </summary>
public sealed class BrowserTab : IDisposable
{
    /// <summary>Recovering more often than this means something is badly wrong; ask the user instead of looping.</summary>
    private static readonly TimeSpan MinimumTimeBetweenAutoRecoveries = TimeSpan.FromSeconds(30);

    /// <summary>Internal name of WebView2's default "Open link in new window" context-menu item.</summary>
    private const string OpenLinkInNewWindowItemName = "openLinkInNewWindow";

    private readonly BrowserEnvironment _environment;
    private readonly ExtensionManager _extensions;
    private readonly DownloadInterceptor _downloads;
    private readonly ThemeService _theme;
    private readonly AppLogger _log;
    private readonly MediaSniffer _media;
    private readonly Grid _host = new();

    private WebView2? _webView;
    private CoreWebView2? _core;
    private CoreWebView2Environment? _attachedEnvironment;
    private Uri? _pendingNavigation;
    private ulong _currentNavigationId;
    private Uri? _currentNavigationUri;
    private DateTime _lastAutoRecoveryUtc = DateTime.MinValue;
    private int _faviconVersion;
    private bool _hasCommittedPage;
    private bool _disposed;

    public BrowserTab(BrowserEnvironment environment, ExtensionManager extensions, DownloadInterceptor downloads, ThemeService theme, Func<bool> detectVideos, AppLogger log)
    {
        _environment = environment;
        _extensions = extensions;
        _downloads = downloads;
        _theme = theme;
        _log = log;
        _media = new MediaSniffer(log, detectVideos);
        _media.Changed += OnMediaChanged;
        _theme.ThemeChanged += OnThemeChanged;
    }

    /// <summary>The element to place in the window. Its child WebView2 control may be swapped during recovery.</summary>
    public FrameworkElement View => _host;

    public bool IsReady => _core is not null;
    public string? Url { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public ImageSource? Favicon { get; private set; }
    public bool IsLoading { get; private set; }
    public bool CanGoBack { get; private set; }
    public bool CanGoForward { get; private set; }

    /// <summary>The page has an element in HTML5 fullscreen (for example a video player).</summary>
    public bool IsPageFullScreen { get; private set; }

    public double ZoomFactor { get; private set; } = ZoomLevels.Default;

    /// <summary>Find-in-page results: number of matches, and the 1-based active match (-1 if not known).</summary>
    public int FindMatchCount { get; private set; }
    public int FindActiveMatch { get; private set; } = -1;

    /// <summary>For <see cref="PopupRequest"/> only: the live CoreWebView2 to hand to WebView2 as a new window.</summary>
    internal CoreWebView2? Core => _core;

    /// <summary>Videos and streams found on the current page (Phase 5).</summary>
    public Core.Media.TabMediaStore Media => _media.Store;

    /// <summary>The list in <see cref="Media"/> changed (at most every 500 ms; at once when a new page clears it).</summary>
    public event EventHandler? MediaChanged;

    /// <summary>Raised when any of the state properties above changed.</summary>
    public event EventHandler? StateChanged;

    /// <summary>A new top-level navigation began (any earlier problem panel is now stale).</summary>
    public event EventHandler? NavigationStarted;

    /// <summary>
    /// A page finished loading (the URL is the final one, after redirects), or a single-page app changed its URL
    /// without loading a new document (YouTube, Gmail…). Used to record history.
    /// </summary>
    public event EventHandler<string>? PageLoaded;

    /// <summary>
    /// The page's icon arrived: the page URL it belongs to, the icon as PNG (WebView2 gives 16 px), the icon file's
    /// own address and the browser's user agent (so a sharper version can be fetched for lists and tabs).
    /// </summary>
    public event EventHandler<FaviconInfo>? FaviconLoaded;

    /// <summary>Something the user must see instead of the page (error, crash, engine failure).</summary>
    public event EventHandler<PageProblem>? ProblemOccurred;

    /// <summary>The user caused the page to open a new window: open it as a tab. Must be answered (see <see cref="PopupRequest"/>).</summary>
    public event EventHandler<PopupRequest>? PopupRequested;

    /// <summary>A popup the user didn't cause was blocked (WebView2 doesn't block them itself; spike F10). The URL may be null.</summary>
    public event EventHandler<Uri?>? PopupBlocked;

    /// <summary>"Open link in new tab" was chosen in the context menu.</summary>
    public event EventHandler<Uri>? OpenInNewTabRequested;

    /// <summary>The page called window.close() (for example a sign-in popup that finished).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// A download started in a tab that never showed a page (a download link opened in a new tab, or Retry): the tab is
    /// blank and can go. Its WebView is kept for the download when the tab closes (see <see cref="Dispose"/>).
    /// </summary>
    public event EventHandler? OnlyDownloaded;

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

    public void Print() => _core?.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);

    public void SetZoom(double zoomFactor)
    {
        if (_webView is not null)
        {
            _webView.ZoomFactor = zoomFactor; // raises ZoomFactorChanged, which updates ZoomFactor
        }
    }

    /// <summary>Starts (or restarts) find-in-page. WebView2's own find bar is suppressed; AJBrowser shows its own.</summary>
    public async Task FindAsync(string term)
    {
        if (_core is null || _attachedEnvironment is null)
        {
            return;
        }

        if (string.IsNullOrEmpty(term))
        {
            StopFind();
            return;
        }

        var options = _attachedEnvironment.CreateFindOptions();
        options.FindTerm = term;
        options.SuppressDefaultFindDialog = true;
        options.ShouldHighlightAllMatches = true;

        try
        {
            await _core.Find.StartAsync(options);
        }
        catch (Exception ex)
        {
            _log.Debug($"Find failed: {ex.GetType().Name}");
        }
    }

    public void FindNext() => _core?.Find.FindNext();

    public void FindPrevious() => _core?.Find.FindPrevious();

    public void StopFind()
    {
        _core?.Find.Stop();
        FindMatchCount = 0;
        FindActiveMatch = -1;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

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
        _media.Changed -= OnMediaChanged;
        DetachWebView(keepForDownloads: true);
        _media.Dispose();
    }

    /// <summary>
    /// The Cookie header this tab's profile would send to <paramref name="url"/> right now ("a=1; b=2"), or null.
    /// For downloading a detected video the way the page's player would (plan §6.2); never logged or stored.
    /// </summary>
    public async Task<string?> GetCookieHeaderAsync(Uri url)
    {
        if (_core is null)
        {
            return null;
        }

        try
        {
            var cookies = await _core.CookieManager.GetCookiesAsync(url.AbsoluteUri);
            return cookies.Count == 0 ? null : string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
        }
        catch (Exception ex)
        {
            _log.Debug($"Cookies unavailable: {ex.GetType().Name}");
            return null;
        }
    }

    private void OnMediaChanged(object? sender, EventArgs e) => MediaChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Video detection was switched off: drop what was found.</summary>
    public void ClearMedia() => _media.Clear();

    /// <summary>"Download video" was chosen on a &lt;video&gt;/&lt;audio&gt; element with a real (non-blob) address.</summary>
    public event EventHandler<Uri>? DownloadVideoRequested;

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
        // White, like Chrome: pages that don't paint a background (plain HTML, many docs) assume white behind
        // their default black text. A dark colour here made such pages unreadable in dark mode.
        var webView = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.White };
        _host.Children.Add(webView);
        _webView = webView;

        try
        {
            // Completes once the control has a window handle (once it is part of the window's visual tree).
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
        Configure(webView, _core);
        _environment.RememberProfile(_core.Profile);

        // The ad blocker must be installed before the first page loads, or that page still shows ads.
        // Only the first WebViews actually wait (a few seconds on the very first run); never throws.
        await _extensions.PrepareAsync(_core);
        if (_disposed || !ReferenceEquals(_webView, webView))
        {
            return;
        }

        if (_pendingNavigation is { } target)
        {
            _pendingNavigation = null;
            Navigate(target);
        }
    }

    /// <summary>
    /// ALL per-WebView setup goes here, and only here. WebViews created for popups or after a crash don't
    /// inherit anything from other WebViews (spike F10), so every WebView must pass through this.
    /// </summary>
    private void Configure(WebView2 webView, CoreWebView2 core)
    {
        var settings = core.Settings;
#if !DEBUG
        settings.AreDevToolsEnabled = false; // F12 DevTools stay available in Debug builds.
#endif
        settings.IsStatusBarEnabled = true; // WebView2's own link-hover bubble (bottom-left) is all we need.

        core.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Balanced; // Strict becomes a setting later (spike F8).
        ApplyColorScheme(core);

        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.SourceChanged += OnSourceChanged;
        core.HistoryChanged += OnHistoryChanged;
        core.DocumentTitleChanged += OnDocumentTitleChanged;
        core.FaviconChanged += OnFaviconChanged;
        core.ContainsFullScreenElementChanged += OnContainsFullScreenElementChanged;
        core.ProcessFailed += OnProcessFailed;
        core.NewWindowRequested += OnNewWindowRequested;
        core.WindowCloseRequested += OnWindowCloseRequested;
        core.ContextMenuRequested += OnContextMenuRequested;
        core.Find.MatchCountChanged += OnFindResultsChanged;
        core.Find.ActiveMatchIndexChanged += OnFindResultsChanged;
        webView.ZoomFactorChanged += OnZoomFactorChanged;
        _downloads.Attach(core);
        core.DownloadStarting += OnDownloadStartingInTab; // after the interceptor's handler
        _media.Attach(core);

        ZoomFactor = webView.ZoomFactor;
        UpdateState(core);
    }

    /// <param name="keepForDownloads">
    /// Closing the tab: if this WebView still owns downloads, hand it to <see cref="DownloadInterceptor.Park"/> instead
    /// of disposing it (disposing freezes them, spike F5). Engine restarts pass false: the old engine is gone anyway.
    /// </param>
    private void DetachWebView(bool keepForDownloads = false)
    {
        var detachedCore = _core;
        var park = keepForDownloads && _core is not null && _webView is not null && _downloads.OwnsDownloads(_core);

        if (_core is not null)
        {
            try
            {
                _core.NavigationStarting -= OnNavigationStarting;
                _core.NavigationCompleted -= OnNavigationCompleted;
                _core.SourceChanged -= OnSourceChanged;
                _core.HistoryChanged -= OnHistoryChanged;
                _core.DocumentTitleChanged -= OnDocumentTitleChanged;
                _core.FaviconChanged -= OnFaviconChanged;
                _core.ContainsFullScreenElementChanged -= OnContainsFullScreenElementChanged;
                _core.ProcessFailed -= OnProcessFailed;
                _core.NewWindowRequested -= OnNewWindowRequested;
                _core.WindowCloseRequested -= OnWindowCloseRequested;
                _core.ContextMenuRequested -= OnContextMenuRequested;
                _core.Find.MatchCountChanged -= OnFindResultsChanged;
                _core.Find.ActiveMatchIndexChanged -= OnFindResultsChanged;
                _core.DownloadStarting -= OnDownloadStartingInTab;
                _downloads.Detach(_core);
                _media.Detach(_core);
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
            _webView.ZoomFactorChanged -= OnZoomFactorChanged;
            _host.Children.Remove(_webView);
            if (park)
            {
                _downloads.Park(_webView, detachedCore!);
            }
            else
            {
                try
                {
                    _webView.Dispose();
                }
                catch (Exception ex)
                {
                    _log.Debug($"Disposing a dead WebView: {ex.GetType().Name}");
                }
            }

            _webView = null;
        }

        _attachedEnvironment = null;
        if (IsLoading || IsPageFullScreen)
        {
            IsLoading = false;
            IsPageFullScreen = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---------------------------------------------------------------- navigation events

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
        if (e.IsSuccess)
        {
            _hasCommittedPage = true;
        }

        if (sender is CoreWebView2 core)
        {
            UpdateState(core);
            if (e.IsSuccess && !string.IsNullOrEmpty(core.Source))
            {
                PageLoaded?.Invoke(this, core.Source);
            }
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

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        var core = (CoreWebView2)sender!;
        UpdateState(core);

        // Same-document change (history.pushState, #fragment): no NavigationCompleted follows, so report it here.
        if (!e.IsNewDocument && !string.IsNullOrEmpty(core.Source))
        {
            PageLoaded?.Invoke(this, core.Source);
        }
    }

    private void OnHistoryChanged(object? sender, object e) => UpdateState((CoreWebView2)sender!);

    private void OnDocumentTitleChanged(object? sender, object e) => UpdateState((CoreWebView2)sender!);

    // ---------------------------------------------------------------- page events

    private async void OnFaviconChanged(object? sender, object e)
    {
        // async void is fine for an event handler as long as nothing can escape: everything is caught below.
        var core = (CoreWebView2)sender!;
        var version = ++_faviconVersion;
        ImageSource? icon = null;
        byte[]? png = null;
        string? pageUrl = null;
        string? iconUrl = null;
        string? userAgent = null;

        try
        {
            if (!string.IsNullOrEmpty(core.FaviconUri))
            {
                pageUrl = core.Source;
                iconUrl = core.FaviconUri;
                userAgent = core.Settings.UserAgent;
                using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
                if (stream is not null)
                {
                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer);
                    png = buffer.ToArray();
                    icon = FaviconImages.Decode(png);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"Favicon could not be loaded: {ex.GetType().Name}");
        }

        // Ignore results that arrive after a newer favicon change or after the WebView was replaced.
        if (version != _faviconVersion || !ReferenceEquals(core, _core))
        {
            return;
        }

        Favicon = icon;
        StateChanged?.Invoke(this, EventArgs.Empty);

        if (png is not null && icon is not null)
        {
            FaviconLoaded?.Invoke(this, new FaviconInfo(pageUrl, png, iconUrl, userAgent));
        }
    }

    private void OnContainsFullScreenElementChanged(object? sender, object e)
    {
        IsPageFullScreen = ((CoreWebView2)sender!).ContainsFullScreenElement;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnZoomFactorChanged(object? sender, EventArgs e)
    {
        ZoomFactor = ((WebView2)sender!).ZoomFactor;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnFindResultsChanged(object? sender, object e)
    {
        var find = (CoreWebView2Find)sender!;
        FindMatchCount = find.MatchCount;
        FindActiveMatch = find.ActiveMatchIndex;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        var target = Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) ? uri : null;

        // WebView2 doesn't block popups the user didn't cause (spike F10); IsUserInitiated is reliable (F14).
        if (!e.IsUserInitiated || PopupRequested is null)
        {
            e.Handled = true; // cancelled: no window opens, window.open() returns null in the page
            _log.Info($"Blocked a popup (user-initiated: {e.IsUserInitiated}) to {Redactor.Url(target)}");
            PopupBlocked?.Invoke(this, target);
            return;
        }

        // Never let WebView2 open its own window: it would skip Configure() (spike F10). The deferral makes
        // WebView2 wait until the new tab's WebView is ready to be handed over.
        PopupRequested.Invoke(this, new PopupRequest(e, e.GetDeferral(), target));
    }

    /// <summary>A navigation that becomes a download never completes successfully, so a tab with no page yet stays blank.</summary>
    private void OnDownloadStartingInTab(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        if (!_hasCommittedPage)
        {
            _host.Dispatcher.BeginInvoke(() => OnlyDownloaded?.Invoke(this, EventArgs.Empty));
        }
    }

    private void OnWindowCloseRequested(object? sender, object e) =>
        _host.Dispatcher.BeginInvoke(() => CloseRequested?.Invoke(this, EventArgs.Empty));

    private void OnContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        var items = e.MenuItems;

        // WebView2's "Open link in new window" would open a tab anyway; replace it with clear tab wording.
        var insertAt = 0;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Name == OpenLinkInNewWindowItemName)
            {
                items.RemoveAt(i);
                insertAt = i;
                break;
            }
        }

        var target = e.ContextMenuTarget;
        AddDownloadVideoItem(target, items);

        if (_attachedEnvironment is null
            || !target.HasLinkUri
            || !Uri.TryCreate(target.LinkUri, UriKind.Absolute, out var link)
            || (link.Scheme != Uri.UriSchemeHttps && link.Scheme != Uri.UriSchemeHttp))
        {
            return;
        }

        var item = _attachedEnvironment.CreateContextMenuItem("Open link in new tab", null, CoreWebView2ContextMenuItemKind.Command);
        item.CustomItemSelected += (_, _) => OpenInNewTabRequested?.Invoke(this, link);
        items.Insert(insertAt, item);
    }

    /// <summary>
    /// Right-click on a video (or audio) element whose source is a real http(s) address: "Download video" downloads it
    /// with AJBrowser's own downloader (headers and cookies like the page). Blob sources (streaming players) get nothing:
    /// they aren't files (plan §6.3).
    /// </summary>
    private void AddDownloadVideoItem(CoreWebView2ContextMenuTarget target, IList<CoreWebView2ContextMenuItem> items)
    {
        if (_attachedEnvironment is null
            || target.Kind is not (CoreWebView2ContextMenuTargetKind.Video or CoreWebView2ContextMenuTargetKind.Audio)
            || !target.HasSourceUri
            || !Uri.TryCreate(target.SourceUri, UriKind.Absolute, out var source)
            || (source.Scheme != Uri.UriSchemeHttps && source.Scheme != Uri.UriSchemeHttp))
        {
            return;
        }

        var label = target.Kind == CoreWebView2ContextMenuTargetKind.Audio ? "Download audio with AJBrowser" : "Download video with AJBrowser";
        var item = _attachedEnvironment.CreateContextMenuItem(label, null, CoreWebView2ContextMenuItemKind.Command);
        item.CustomItemSelected += (_, _) => _host.Dispatcher.BeginInvoke(() => DownloadVideoRequested?.Invoke(this, source));
        items.Insert(0, item);
    }

    // ---------------------------------------------------------------- crashes

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

}

/// <summary>A page's icon as WebView2 delivered it, plus what's needed to fetch a sharper one.</summary>
public sealed record FaviconInfo(string? PageUrl, byte[] Png, string? IconUrl, string? UserAgent);
