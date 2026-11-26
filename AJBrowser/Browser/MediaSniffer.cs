using System.Windows.Threading;
using AJBrowser.Core.Logging;
using AJBrowser.Core.Media;
using AJBrowser.Core.Security;
using Microsoft.Web.WebView2.Core;

namespace AJBrowser.Browser;

/// <summary>
/// Finds videos in one tab (plan Phase 5 / §6.2) by watching its network responses, and keeps them in a
/// <see cref="TabMediaStore"/> for the current page.
/// <list type="bullet">
/// <item><b>Responses</b> (WebResourceResponseReceived): only the URL, status and a few headers are read, never bodies.
/// The cheap checks run first, so the hundreds of ordinary responses on a page cost almost nothing.</item>
/// <item><b>How it was requested:</b> that event doesn't say whether the browser's own player or page script asked for
/// a file, so a request filter for the <c>Media</c> resource context records what native &lt;video&gt;/&lt;audio&gt;
/// elements load. Everything else counts as script-fetched (how streaming players load pieces).</item>
/// <item><b>Hints:</b> a small script reports each &lt;video&gt;'s currentSrc, size and duration. Untrusted: parsed
/// strictly by <see cref="VideoHint.TryParse"/>, and it only enriches files already seen on the network.</item>
/// <item>The list clears when a new document commits, and the UI hears about changes at most every 500 ms.</item>
/// </list>
/// Lives as long as its <see cref="BrowserTab"/>; attached to each WebView the tab creates. UI thread only.
/// </summary>
public sealed class MediaSniffer : IDisposable
{
    /// <summary>The hint script. Top frame only; sends a small JSON message when a media element knows its size.</summary>
    internal const string HintScript = """
        (() => {
          if (window.top !== window || !window.chrome || !window.chrome.webview) return;
          const send = (m) => {
            try {
              window.chrome.webview.postMessage(JSON.stringify({
                type: 'ajb-video',
                currentSrc: String(m.currentSrc || '').slice(0, 2048),
                videoWidth: m.videoWidth || 0,
                videoHeight: m.videoHeight || 0,
                duration: Number.isFinite(m.duration) ? m.duration : 0
              }));
            } catch (e) { }
          };
          document.addEventListener('loadedmetadata', (e) => {
            if (e.target instanceof HTMLMediaElement) send(e.target);
          }, true);
        })();
        """;

    private const int MaxNativeKeys = 500;
    private static readonly TimeSpan NotifyInterval = TimeSpan.FromMilliseconds(500);

    private readonly AppLogger _log;
    private readonly HashSet<string> _nativeKeys = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _notifyTimer;
    private CoreWebView2? _core;

    public MediaSniffer(AppLogger log)
    {
        _log = log;
        _notifyTimer = new DispatcherTimer { Interval = NotifyInterval };
        _notifyTimer.Tick += (_, _) =>
        {
            _notifyTimer.Stop();
            Changed?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>The current page's media.</summary>
    public TabMediaStore Store { get; } = new();

    /// <summary>The visible list changed (throttled to every 500 ms).</summary>
    public event EventHandler? Changed;

    public void Attach(CoreWebView2 core)
    {
        _core = core;
        try
        {
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Media);
            core.WebResourceRequested += OnMediaRequested;
            core.WebResourceResponseReceived += OnResponseReceived;
            core.WebMessageReceived += OnWebMessageReceived;
            core.ContentLoading += OnContentLoading;
            core.SourceChanged += OnSourceChanged;
            _ = AddHintScriptAsync(core);
        }
        catch (Exception ex)
        {
            _log.Warn($"Video detection unavailable in this tab: {ex.GetType().Name}");
        }
    }

    public void Detach(CoreWebView2 core)
    {
        try
        {
            core.WebResourceRequested -= OnMediaRequested;
            core.WebResourceResponseReceived -= OnResponseReceived;
            core.WebMessageReceived -= OnWebMessageReceived;
            core.ContentLoading -= OnContentLoading;
            core.SourceChanged -= OnSourceChanged;
            core.RemoveWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Media);
        }
        catch (Exception)
        {
            // The WebView is already gone (browser process ended).
        }

        if (ReferenceEquals(_core, core))
        {
            _core = null;
        }
    }

    public void Dispose()
    {
        _notifyTimer.Stop();
        Changed = null;
    }

    private async Task AddHintScriptAsync(CoreWebView2 core)
    {
        try
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(HintScript);
        }
        catch (Exception ex)
        {
            _log.Debug($"Video hint script not added: {ex.GetType().Name}");
        }
    }

    // ---------------------------------------------------------------- events

    /// <summary>A native media element asked for this file (the filter only lets Media requests through).</summary>
    private void OnMediaRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) && _nativeKeys.Count < MaxNativeKeys)
            {
                _nativeKeys.Add(MediaClassifier.KeyOf(uri));
            }
        }
        catch (Exception)
        {
            // Never let detection disturb a request.
        }
    }

    private void OnResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        try
        {
            var request = e.Request;
            if (!Uri.TryCreate(request.Uri, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
            {
                return;
            }

            var response = e.Response;
            var status = response.StatusCode;
            if (status is not (200 or 206))
            {
                return;
            }

            // Cheap first pass with just the type and the address: most responses stop here.
            var headers = response.Headers;
            var contentType = Header(headers, "Content-Type");
            var context = _nativeKeys.Contains(MediaClassifier.KeyOf(url)) ? "Media" : "Fetch";
            var first = MediaClassifier.Classify(new MediaObservation(url, status, contentType, null, null, context));
            if (first.Kind is MediaKind.None or MediaKind.Segment)
            {
                return;
            }

            long? length = long.TryParse(Header(headers, "Content-Length"), out var parsed) ? parsed : null;
            var observation = new MediaObservation(url, status, contentType, length, Header(headers, "Content-Range"), context, Header(headers, "Content-Disposition"));
            var pageUrl = Uri.TryCreate((sender as CoreWebView2)?.Source, UriKind.Absolute, out var page) ? page : null;
            var requestContext = RequestContext.FromObservedRequest(request.Headers, pageUrl);

            if (Store.Add(observation, requestContext))
            {
                _log.Debug($"Media found: {first.Kind} {first.Container} {Redactor.MediaUrl(url)}");
                ScheduleNotify();
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"Media response skipped: {ex.GetType().Name}");
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? message;
        try
        {
            message = e.TryGetWebMessageAsString();
        }
        catch (Exception)
        {
            return; // not a string message: not ours
        }

        if (VideoHint.TryParse(message) is { } hint && Store.ApplyHint(hint))
        {
            ScheduleNotify();
        }
    }

    /// <summary>A new document committed: the old page's media is gone.</summary>
    private void OnContentLoading(object? sender, CoreWebView2ContentLoadingEventArgs e)
    {
        _nativeKeys.Clear();
        if (Store.VisibleCount > 0 || Store.Items.Count > 0)
        {
            Store.Reset();
            _notifyTimer.Stop();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            Store.Reset();
        }
    }

    /// <summary>A single-page app changed its address without a new document: keep the list, flag what comes next.</summary>
    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        if (!e.IsNewDocument)
        {
            Store.MarkSpaNavigation();
        }
    }

    private void ScheduleNotify()
    {
        if (!_notifyTimer.IsEnabled)
        {
            _notifyTimer.Start();
        }
    }

    private static string? Header(CoreWebView2HttpResponseHeaders headers, string name) =>
        headers.Contains(name) ? headers.GetHeader(name) : null;
}
