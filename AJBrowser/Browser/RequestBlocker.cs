using System.IO;
using AJBrowser.Core.Blocking;
using AJBrowser.Core.Logging;
using AJBrowser.Core.Security;
using Microsoft.Web.WebView2.Core;

namespace AJBrowser.Browser;

/// <summary>
/// AJBrowser's own ad-block engine in one tab (Phase 8c): checks every network request against the filter lists and
/// answers blocked ones itself, and has <see cref="CosmeticInjector"/> put scriptlets and hide rules into pages.
/// <list type="bullet">
/// <item>The request filter is only registered while the engine is active on this page, so with uBO Lite chosen (or
/// blocking off) requests cost nothing extra.</item>
/// <item>Whether to block is decided once per navigation (<see cref="AdBlockService.EngineFor"/>: switch, engine, per-site
/// setting) and re-decided when any of those change.</item>
/// <item>The handler runs on the UI thread for every request: it reads the address and context once and does no other
/// work for requests that pass (F6: ~25–70 µs per request; the engine itself p99 37 µs, F25).</item>
/// <item>Top-level pages are never blocked (that would show an error page); frames are, as <c>subdocument</c>.</item>
/// <item>The first top-level visit to a site in a tab is cancelled and started again a few ms later, once that site's
/// scripts are registered (WebView2 only gives scripts to navigations that start after them; see
/// <see cref="CosmeticInjector"/>). Back/Forward and form posts aren't restarted.</item>
/// </list>
/// UI thread only.
/// </summary>
internal sealed class RequestBlocker : IDisposable
{
    private const CoreWebView2WebResourceRequestSourceKinds WorkerKinds =
        CoreWebView2WebResourceRequestSourceKinds.ServiceWorker | CoreWebView2WebResourceRequestSourceKinds.SharedWorker;

    private readonly AdBlockService _service;
    private readonly AppLogger _log;
    private readonly CosmeticInjector _injector;

    private CoreWebView2? _core;
    private CoreWebView2Environment? _environment;
    private IFilterEngine? _engine;
    private string? _pageUrl;
    private string? _pendingTopUrl;
    private bool _filterRegistered;
    private bool _workerFilterRegistered;
    private bool _isWorkerOwner;
    private int _checked;
    private int _navigationToken;
    private string? _replayUrl;
    private bool _disposed;

    public RequestBlocker(AdBlockService service, AppLogger log)
    {
        _service = service;
        _log = log;
        _injector = new CosmeticInjector(log);
        _service.BlockingChanged += OnBlockingChanged;
    }

    /// <summary>Requests blocked (or answered with a stand-in) on the current page. 8d shows it on the shield.</summary>
    public int BlockedCount { get; private set; }

    public void Attach(CoreWebView2 core, CoreWebView2Environment environment)
    {
        _core = core;
        _environment = environment;
        _pageUrl = core.Source;
        try
        {
            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            core.SourceChanged += OnSourceChanged;
            core.WebResourceRequested += OnWebResourceRequested;
            _injector.Attach(core);
        }
        catch (Exception ex)
        {
            _log.Warn($"Ad blocking unavailable in this tab: {ex.GetType().Name}");
            return;
        }

        _service.Register(this);
        Refresh();
    }

    public void Detach(CoreWebView2 core)
    {
        _service.Unregister(this);
        try
        {
            core.NavigationStarting -= OnNavigationStarting;
            core.NavigationCompleted -= OnNavigationCompleted;
            core.SourceChanged -= OnSourceChanged;
            core.WebResourceRequested -= OnWebResourceRequested;
        }
        catch (Exception)
        {
            // the WebView is gone
        }

        UpdateFilters(core, page: false, workers: false);
        _injector.Detach(core);
        if (ReferenceEquals(_core, core))
        {
            _core = null;
            _environment = null;
            _engine = null;
        }
    }

    /// <summary>This tab also filters service-worker and shared-worker requests (set by <see cref="AdBlockService"/>).</summary>
    internal void SetWorkerOwner(bool owner)
    {
        _isWorkerOwner = owner;
        if (_core is { } core)
        {
            UpdateFilters(core, _filterRegistered, owner && _service.ActiveEngine is not null);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _service.BlockingChanged -= OnBlockingChanged;
        if (_core is { } core)
        {
            Detach(core);
        }
    }

    // ---------------------------------------------------------------- state

    /// <summary>Re-decides blocking for the current page and (un)registers the request filter to match.</summary>
    private void Refresh()
    {
        if (_core is not { } core)
        {
            return;
        }

        var engine = _service.EngineFor(_pendingTopUrl ?? _pageUrl);
        if (!ReferenceEquals(engine, _engine))
        {
            _injector.Clear();
            _engine = engine;
        }

        UpdateFilters(core, page: engine is not null, workers: _isWorkerOwner && _service.ActiveEngine is not null);
    }

    private void UpdateFilters(CoreWebView2 core, bool page, bool workers)
    {
        try
        {
            if (page != _filterRegistered)
            {
                if (page)
                {
                    core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.Document);
                }
                else
                {
                    core.RemoveWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.Document);
                }

                _filterRegistered = page;
            }

            if (workers != _workerFilterRegistered)
            {
                if (workers)
                {
                    core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, WorkerKinds);
                }
                else
                {
                    core.RemoveWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, WorkerKinds);
                }

                _workerFilterRegistered = workers;
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"Request filter not changed: {ex.GetType().Name}");
        }
    }

    private void OnBlockingChanged(object? sender, EventArgs e) => Refresh();

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        _pendingTopUrl = e.Uri;
        if (!e.IsRedirected)
        {
            BlockedCount = 0;
            _checked = 0;
        }

        var navigation = ++_navigationToken;
        var replayed = string.Equals(e.Uri, _replayUrl, StringComparison.Ordinal);
        _replayUrl = null;
        Refresh();
        if (_engine is not { } engine || !Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
        {
            return;
        }

        if (_injector.HasScriptsFor(engine, uri.Host))
        {
            return;
        }

        // A site whose scripts aren't registered yet: they only reach navigations that start after the registration
        // finished (see CosmeticInjector), so hold this one back and start it again once they're in place. Registering
        // must happen after this handler returns: started inside it, WebView2 finishes the registration only when the
        // navigation has ended (found in the real app: always ~2 s, against 2–3 ms outside the handler).
        var restart = !replayed && CanRestart(e);
        if (restart)
        {
            e.Cancel = true;
        }

        _ = RegisterThenRestartAsync(engine, e.Uri, restart ? Header(e.RequestHeaders, "Referer") : null, restart, navigation);
    }

    /// <summary>
    /// Only plain page loads can be started again without changing what happens: not Back/Forward (that would add a
    /// history entry instead of moving through it) and not form posts (the posted data can't be sent again).
    /// </summary>
    private static bool CanRestart(CoreWebView2NavigationStartingEventArgs e) =>
        e.NavigationKind != CoreWebView2NavigationKind.BackOrForward
        && Header(e.RequestHeaders, "Content-Type") is null
        && (e.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || e.Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase));

    private async Task RegisterThenRestartAsync(IFilterEngine engine, string url, string? referer, bool restart, int navigation)
    {
        await Task.Yield(); // out of the NavigationStarting handler first (see above)
        if (_core is null || !ReferenceEquals(engine, _engine))
        {
            return;
        }

        _ = _injector.PrepareDocument(engine, url);
        if (!restart)
        {
            return;
        }

        // Restart as soon as the calls are out. WebView2 puts the scripts in its list when called, but answers only after
        // the cancelled navigation has been replaced, so waiting for the answer would wait for this restart (found in
        // the real app: 2–23 s when waiting, 5–16 ms when not, and the first visit gets its scripts).
        await Task.Yield();
        if (navigation != _navigationToken || _core is not { } core || _environment is not { } environment)
        {
            return; // the user went somewhere else meanwhile
        }

        _replayUrl = url;
        try
        {
            var headers = referer is null ? "" : "Referer: " + referer;
            core.NavigateWithWebResourceRequest(environment.CreateWebResourceRequest(url, "GET", null, headers));
        }
        catch (Exception ex)
        {
            _log.Debug($"Restarting a navigation with its Referer failed ({ex.GetType().Name}); plain navigation instead");
            try
            {
                core.Navigate(url);
            }
            catch (Exception)
            {
                _replayUrl = null;
            }
        }
    }

    private static string? Header(CoreWebView2HttpRequestHeaders headers, string name)
    {
        try
        {
            return headers.Contains(name) ? headers.GetHeader(name) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        _pageUrl = (sender as CoreWebView2)?.Source;
        _pendingTopUrl = null;
        Refresh();
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_engine is not null && _checked > 0)
        {
            _log.Debug($"Ad blocking on {Redactor.Url(Uri.TryCreate(_pageUrl, UriKind.Absolute, out var u) ? u : null)}: "
                       + $"{BlockedCount} of {_checked} requests blocked, scripts for {_injector.RegisteredHosts} host(s)");
        }
    }

    // ---------------------------------------------------------------- requests

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var engine = _engine;
        if (engine is null || _environment is null)
        {
            // Worker requests also arrive while this tab's own page isn't filtered.
            engine = _service.ActiveEngine;
            if (engine is null || _environment is null || e.RequestedSourceKind == CoreWebView2WebResourceRequestSourceKinds.Document)
            {
                return;
            }
        }

        try
        {
            var context = e.ResourceContext;
            var request = e.Request;
            var url = request.Uri;
            _checked++;

            if (context == CoreWebView2WebResourceContext.Document && e.RequestedSourceKind == CoreWebView2WebResourceRequestSourceKinds.Document)
            {
                if (IsTopLevel(url))
                {
                    return; // never blocked; its scripts were handled in NavigationStarting
                }

                // A frame: block it like any request. Otherwise register its site's scripts: too late for this frame
                // (see CosmeticInjector), in place for the next page that embeds it. Holding the request back wouldn't
                // help: WebView2 only finishes a registration once the navigation in progress has ended.
                if (!Answer(e, engine.Check(url, _pageUrl ?? "", RequestKind.Subdocument)))
                {
                    _ = _injector.PrepareDocument(engine, url);
                }

                return;
            }

            var source = e.RequestedSourceKind == CoreWebView2WebResourceRequestSourceKinds.Document
                ? _pageUrl ?? ""
                : WorkerSource(request, url);
            Answer(e, engine.Check(url, source, KindOf(context)));
        }
        catch (Exception ex)
        {
            _log.Debug($"Request check skipped: {ex.GetType().Name}");
        }
    }

    /// <summary>Applies a decision; true when the request was answered (blocked or replaced).</summary>
    private bool Answer(CoreWebView2WebResourceRequestedEventArgs e, FilterDecision decision)
    {
        if (decision.RedirectDataUrl is { } redirect && RedirectResource.TryDecode(redirect) is { } resource)
        {
            e.Response = _environment!.CreateWebResourceResponse(
                new MemoryStream(resource.Body, writable: false),
                200,
                "OK",
                $"Content-Type: {resource.ContentType}\r\nAccess-Control-Allow-Origin: *");
            BlockedCount++;
            return true;
        }

        if (decision.ShouldBlock)
        {
            e.Response = _environment!.CreateWebResourceResponse(null, 403, "Blocked", "");
            BlockedCount++;
            return true;
        }

        return false;
    }

    /// <summary>Is this document request the tab's top-level navigation (not a frame)?</summary>
    private bool IsTopLevel(string url)
    {
        if (_pendingTopUrl is not { } top)
        {
            return false;
        }

        var hash = top.IndexOf('#');
        var topWithoutFragment = hash < 0 ? top.AsSpan() : top.AsSpan(0, hash);
        return url.AsSpan().Equals(topWithoutFragment, StringComparison.Ordinal);
    }

    /// <summary>Service/shared workers belong to no page: their page is the Referer, else the request's own site.</summary>
    private static string WorkerSource(CoreWebView2WebResourceRequest request, string url)
    {
        try
        {
            var headers = request.Headers;
            if (headers.Contains("Referer") && headers.GetHeader("Referer") is { Length: > 0 } referer)
            {
                return referer;
            }
        }
        catch (Exception)
        {
            // fall through
        }

        return url;
    }

    internal static RequestKind KindOf(CoreWebView2WebResourceContext context) => context switch
    {
        CoreWebView2WebResourceContext.Document => RequestKind.Subdocument,
        CoreWebView2WebResourceContext.Stylesheet => RequestKind.Stylesheet,
        CoreWebView2WebResourceContext.Image => RequestKind.Image,
        CoreWebView2WebResourceContext.Media => RequestKind.Media,
        CoreWebView2WebResourceContext.Font => RequestKind.Font,
        CoreWebView2WebResourceContext.Script => RequestKind.Script,
        CoreWebView2WebResourceContext.XmlHttpRequest => RequestKind.XmlHttpRequest,
        CoreWebView2WebResourceContext.Fetch => RequestKind.XmlHttpRequest,
        CoreWebView2WebResourceContext.Websocket => RequestKind.WebSocket,
        CoreWebView2WebResourceContext.Ping => RequestKind.Ping,
        _ => RequestKind.Other, // text tracks, event sources, manifests, signed exchanges, CSP reports
    };
}
