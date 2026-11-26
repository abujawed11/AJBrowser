using System.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace AJBrowser.Diagnostics;

/// <summary>
/// Spike for V4/V11: intercepts every request with WebResourceRequested and measures the cost.
/// The host-suffix blocklist is a TEST FIXTURE ONLY, not a real ad blocker (see plan §6.5).
/// </summary>
internal sealed class BlockingProbe
{
    private static readonly string[] TestBlockList =
    [
        "doubleclick.net", "googlesyndication.com", "googleadservices.com", "adnxs.com", "taboola.com",
        "outbrain.com", "criteo.com", "criteo.net", "amazon-adsystem.com", "scorecardresearch.com",
        "google-analytics.com", "googletagmanager.com", "pubmatic.com", "rubiconproject.com", "casalemedia.com",
    ];

    private readonly List<double> _handlerMicros = [];
    private CoreWebView2? _core;
    private CoreWebView2Environment? _environment;

    public bool IsAttached => _core is not null;
    public bool BlockingEnabled { get; set; }
    public int Intercepted { get; private set; }
    public int Blocked { get; private set; }
    public Dictionary<CoreWebView2WebResourceRequestSourceKinds, int> BySourceKind { get; } = [];
    public Dictionary<CoreWebView2WebResourceContext, int> ByContext { get; } = [];

    public double HandlerP50 => Stats.Percentile(_handlerMicros, 50);
    public double HandlerP99 => Stats.Percentile(_handlerMicros, 99);
    public double HandlerMax => _handlerMicros.Count == 0 ? 0 : _handlerMicros.Max();

    public void Attach(CoreWebView2 core, CoreWebView2Environment environment)
    {
        if (IsAttached)
        {
            return;
        }

        _core = core;
        _environment = environment;

        // Three-argument overload (ICoreWebView2_22): also covers service workers, shared workers and iframes.
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += OnWebResourceRequested;
    }

    public void Detach()
    {
        if (_core is null)
        {
            return;
        }

        try
        {
            _core.WebResourceRequested -= OnWebResourceRequested;
            _core.RemoveWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        }
        catch (Exception)
        {
            // WebView may already be disposed.
        }

        _core = null;
    }

    public void Reset()
    {
        _handlerMicros.Clear();
        Intercepted = Blocked = 0;
        BySourceKind.Clear();
        ByContext.Clear();
    }

    public string DescribeSources() =>
        string.Join(", ", BySourceKind.OrderByDescending(p => p.Value).Select(p => $"{p.Key}={p.Value}"));

    public string DescribeContexts() =>
        string.Join(", ", ByContext.OrderByDescending(p => p.Value).Take(8).Select(p => $"{p.Key}={p.Value}"));

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var start = Stopwatch.GetTimestamp();
        Intercepted++;

        ByContext[e.ResourceContext] = ByContext.GetValueOrDefault(e.ResourceContext) + 1;
        var sourceKind = e.RequestedSourceKind;
        BySourceKind[sourceKind] = BySourceKind.GetValueOrDefault(sourceKind) + 1;

        if (BlockingEnabled && IsTestBlocked(e.Request.Uri))
        {
            e.Response = _environment!.CreateWebResourceResponse(null, 403, "Blocked", string.Empty);
            Blocked++;
        }

        _handlerMicros.Add(Stopwatch.GetElapsedTime(start).TotalMicroseconds);
    }

    private static bool IsTestBlocked(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host;
        foreach (var domain in TestBlockList)
        {
            if (host.Equals(domain, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
