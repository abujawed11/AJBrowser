using AJBrowser.Core.Blocking;
using AJBrowser.Core.Logging;
using Microsoft.Web.WebView2.Core;

namespace AJBrowser.Browser;

/// <summary>
/// Puts AJBrowser's scriptlets and hide rules into one tab's pages (Phase 8c), for its own ad-block engine.
/// <para><b>Timing.</b> Scriptlets must run before the page's own scripts, which only
/// <c>AddScriptToExecuteOnDocumentCreatedAsync</c> guarantees, and that registers a script for every future document,
/// not for one address. So scripts are registered <i>per host</i>, each guarded by a <c>location.hostname</c> check.
/// A registration only reaches documents whose navigation <i>starts</i> after it completed (found in the real app: a
/// cross-site navigation's new renderer already has the list, even when the registration is made in
/// <c>NavigationStarting</c> or while the document request is held). So <see cref="RequestBlocker"/> holds back the
/// first top-level navigation to a site until <see cref="PrepareDocument"/> finished and then starts it again; frames
/// of a site seen for the first time go without scripts (the next page with that frame has them).
/// The scripts of the last <see cref="MaxHosts"/> hosts stay registered, so Back/Forward and returning to a site
/// (which can't be restarted that way) still get them.</para>
/// <para><b>Generic hiding.</b> The cosmetic script reports the page's class names and ids in batches; this class asks
/// the engine which of them to hide (off the UI thread) and answers the frame that asked. Top frame and its direct
/// child frames only (WebView2 hands out frame objects for those).</para>
/// UI thread only.
/// </summary>
internal sealed class CosmeticInjector
{
    /// <summary>Most hosts with registered scripts per tab (pages and their frames); the least recently used goes first.</summary>
    private const int MaxHosts = 24;

    /// <summary>Most class/id lookups in flight for a tab; further batches are dropped (the page keeps working).</summary>
    private const int MaxPendingLookups = 8;

    private readonly AppLogger _log;
    private readonly Dictionary<string, HostScripts> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CoreWebView2Frame> _frames = [];
    private CoreWebView2? _core;
    private IFilterEngine? _engine;
    private int _generation;
    private int _pendingLookups;
    private long _clock;

    public CosmeticInjector(AppLogger log) => _log = log;

    public void Attach(CoreWebView2 core)
    {
        _core = core;
        core.WebMessageReceived += OnTopMessage;
        core.FrameCreated += OnFrameCreated;
    }

    public void Detach(CoreWebView2 core)
    {
        try
        {
            core.WebMessageReceived -= OnTopMessage;
            core.FrameCreated -= OnFrameCreated;
            foreach (var frame in _frames)
            {
                frame.WebMessageReceived -= OnFrameMessage;
                frame.Destroyed -= OnFrameDestroyed;
            }
        }
        catch (Exception)
        {
            // the WebView is gone
        }

        _frames.Clear();
        _hosts.Clear();
        _generation++;
        if (ReferenceEquals(_core, core))
        {
            _core = null;
        }
    }

    /// <summary>
    /// A document for <paramref name="url"/> is about to load with <paramref name="engine"/> active: makes sure its
    /// host's scripts are registered. The returned task completes once they are (at once when they already were).
    /// </summary>
    public Task PrepareDocument(IFilterEngine engine, string url)
    {
        if (_core is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
        {
            return Task.CompletedTask;
        }

        if (!ReferenceEquals(engine, _engine))
        {
            Clear(); // scripts from another engine (lists changed) are stale
            _engine = engine;
        }

        if (_hosts.TryGetValue(uri.Host, out var existing))
        {
            existing.LastUsed = ++_clock;
            return existing.Ready;
        }

        if (_hosts.Count >= MaxHosts)
        {
            var oldest = _hosts.Values.MinBy(h => h.LastUsed)!;
            _hosts.Remove(oldest.Host);
            Remove(_core, oldest);
        }

        CosmeticResources resources;
        try
        {
            resources = engine.GetCosmeticResources(uri.AbsoluteUri); // ~1 ms once warmed up (F25)
        }
        catch (Exception ex)
        {
            _log.Debug($"Cosmetic lookup failed: {ex.GetType().Name}");
            return Task.CompletedTask;
        }

        var entry = new HostScripts(uri.Host, resources) { LastUsed = ++_clock };
        _hosts[uri.Host] = entry;
        entry.Ready = RegisterAsync(_core, entry);
        return entry.Ready;
    }

    /// <summary>Are scripts for <paramref name="host"/> registered (or being registered) for this engine?</summary>
    public bool HasScriptsFor(IFilterEngine engine, string host) =>
        ReferenceEquals(engine, _engine) && _hosts.ContainsKey(host);

    /// <summary>The engine or the page's blocking state changed: remove every registered script.</summary>
    public void Clear()
    {
        if (_core is not null)
        {
            foreach (var entry in _hosts.Values)
            {
                Remove(_core, entry);
            }
        }

        _hosts.Clear();
        _engine = null;
        _generation++; // answers to generic-hiding batches computed for the old state are dropped
    }

    public int RegisteredHosts => _hosts.Count;

    private async Task RegisterAsync(CoreWebView2 core, HostScripts entry)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var scriptlets = CosmeticScripts.BuildScriptletScript(entry.Host, entry.Resources);
            var cosmetic = CosmeticScripts.BuildCosmeticScript(entry.Host, entry.Resources);
            // Both calls go out at once: WebView2 takes a script into its list when called, but may only report back
            // after the current navigation settled (seconds), so nothing should wait on the first to send the second.
            var calls = new List<Task<string>>(2);
            if (scriptlets is not null)
            {
                calls.Add(core.AddScriptToExecuteOnDocumentCreatedAsync(scriptlets));
            }

            if (cosmetic is not null)
            {
                calls.Add(core.AddScriptToExecuteOnDocumentCreatedAsync(cosmetic));
            }

            entry.Sent = true;
            entry.ScriptIds.AddRange(await Task.WhenAll(calls));

            // Reset or a new WebView while we were registering: take the scripts out again.
            if (!ReferenceEquals(core, _core) || !ReferenceEquals(_hosts.GetValueOrDefault(entry.Host), entry))
            {
                Remove(core, entry);
                return;
            }

            _log.Debug($"Scripts for {entry.Host} registered in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms: {entry.Resources.HideSelectors.Count} hide rules, "
                       + $"{(scriptlets is null ? "no scriptlets" : $"scriptlets {scriptlets.Length / 1024} KB")}, "
                       + $"generic hiding {(entry.Resources.GenericHide ? "off" : "on")}");
        }
        catch (Exception ex)
        {
            _log.Debug($"Script registration failed for {entry.Host}: {ex.GetType().Name}");
        }
    }

    private static void Remove(CoreWebView2 core, HostScripts entry)
    {
        foreach (var id in entry.ScriptIds)
        {
            try
            {
                core.RemoveScriptToExecuteOnDocumentCreated(id);
            }
            catch (Exception)
            {
                // the WebView is gone
            }
        }

        entry.ScriptIds.Clear();
    }

    // ---------------------------------------------------------------- generic hiding messages

    private void OnFrameCreated(object? sender, CoreWebView2FrameCreatedEventArgs e)
    {
        try
        {
            var frame = e.Frame;
            frame.WebMessageReceived += OnFrameMessage;
            frame.Destroyed += OnFrameDestroyed;
            _frames.Add(frame);
        }
        catch (Exception ex)
        {
            _log.Debug($"Frame not tracked: {ex.GetType().Name}");
        }
    }

    private void OnFrameDestroyed(object? sender, object e)
    {
        if (sender is CoreWebView2Frame frame)
        {
            try
            {
                frame.WebMessageReceived -= OnFrameMessage;
                frame.Destroyed -= OnFrameDestroyed;
            }
            catch (Exception)
            {
                // already gone
            }

            _frames.Remove(frame);
        }
    }

    private void OnTopMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e) =>
        HandleMessage(e, json => _core?.PostWebMessageAsJson(json));

    private void OnFrameMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (sender is CoreWebView2Frame frame)
        {
            HandleMessage(e, json =>
            {
                if (frame.IsDestroyed() == 0)
                {
                    frame.PostWebMessageAsJson(json);
                }
            });
        }
    }

    /// <summary>A page's class/id batch (untrusted): look up hide selectors off the UI thread and answer the sender.</summary>
    private async void HandleMessage(CoreWebView2WebMessageReceivedEventArgs e, Action<string> reply)
    {
        // async void: an event handler; everything is caught.
        try
        {
            if (_engine is not { } engine || _pendingLookups >= MaxPendingLookups)
            {
                return;
            }

            string? message;
            try
            {
                message = e.TryGetWebMessageAsString();
            }
            catch (Exception)
            {
                return; // not a string: not ours
            }

            if (!CosmeticScripts.TryParseRequest(message, out var classes, out var ids)
                || !Uri.TryCreate(e.Source, UriKind.Absolute, out var source)
                || !_hosts.TryGetValue(source.Host, out var entry)
                || entry.Resources.GenericHide)
            {
                return;
            }

            var generation = _generation;
            _pendingLookups++;
            string? json;
            try
            {
                var exceptions = entry.Resources.Exceptions;
                json = await Task.Run(() => CosmeticScripts.BuildReply(engine.GetHiddenClassIdSelectors(classes, ids, exceptions)));
            }
            finally
            {
                _pendingLookups--;
            }

            if (json is not null && generation == _generation && ReferenceEquals(engine, _engine))
            {
                reply(json);
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"Generic hiding lookup failed: {ex.GetType().Name}");
        }
    }

    private sealed class HostScripts(string host, CosmeticResources resources)
    {
        public string Host { get; } = host;
        public CosmeticResources Resources { get; } = resources;
        public List<string> ScriptIds { get; } = [];
        public Task Ready { get; set; } = Task.CompletedTask;
        public long LastUsed { get; set; }

        /// <summary>The registration calls have been made (their answers may still be outstanding).</summary>
        public bool Sent { get; set; }
    }
}
