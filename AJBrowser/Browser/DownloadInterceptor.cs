using System.IO;
using System.Windows.Threading;
using AJBrowser.Core.Downloads;
using AJBrowser.Core.Logging;
using AJBrowser.Core.Security;
using AJBrowser.Core.Settings;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AJBrowser.Browser;

/// <summary>
/// Takes over downloads that pages start (links, Content-Disposition, "Save as"), plan Phase 4 / §6.1:
/// <list type="bullet">
/// <item>Hides WebView2's own download flyout (<c>Handled = true</c>): AJBrowser shows its own.</item>
/// <item>Chooses the file: the Downloads folder (or the folder from Settings), a safe and free name from
/// <see cref="FileNamer"/>, or asks with a Save dialog when "Ask where to save" is on.</item>
/// <item>Registers the download with <see cref="DownloadManager"/> and forwards the runtime's progress and state.</item>
/// <item>Pause, resume and cancel of browser downloads go through here, because only the live
/// <see cref="CoreWebView2DownloadOperation"/> can do them. After an app restart the operation is gone (Retry instead).</item>
/// <item><b>Keeps closed tabs' WebViews alive while they own downloads</b> (4c). Disposing a WebView freezes its
/// downloads (spike F5); a WebView taken out of the window but not disposed keeps downloading, even after navigating
/// to about:blank, and pause/resume still work (4c spike, plan §11 F18). <see cref="BrowserTab"/> hands such a WebView
/// to <see cref="Park"/>; it's disposed here once its last download is over.</item>
/// </list>
/// One instance for the whole app; every WebView is attached in <see cref="BrowserTab"/>'s Configure.
/// Runs on the UI thread (all WebView2 events do).
/// </summary>
public sealed class DownloadInterceptor : IDisposable
{
    private readonly DownloadManager _manager;
    private readonly FileNamer _namer;
    private readonly SettingsStore _settings;
    private readonly Func<string, string, string?> _askSavePath;
    private readonly Func<string> _defaultFolder;
    private readonly AppLogger _log;

    /// <summary>Registered downloads whose operation can still be controlled, by download id.</summary>
    private readonly Dictionary<long, Tracked> _live = [];

    /// <summary>Downloads that still need their WebView (running, paused, or waiting for the Save dialog), per WebView.</summary>
    private readonly Dictionary<CoreWebView2, HashSet<Tracked>> _byCore = [];

    /// <summary>WebViews of closed tabs, kept only for their downloads.</summary>
    private readonly Dictionary<CoreWebView2, WebView2> _parked = [];

    /// <param name="askSavePath">Shows a Save dialog for (folder, suggested name); returns the chosen path or null if cancelled.</param>
    /// <param name="defaultFolder">The Windows Downloads folder.</param>
    public DownloadInterceptor(DownloadManager manager, FileNamer namer, SettingsStore settings, Func<string, string, string?> askSavePath, Func<string> defaultFolder, AppLogger log)
    {
        _manager = manager;
        _namer = namer;
        _settings = settings;
        _askSavePath = askSavePath;
        _defaultFolder = defaultFolder;
        _log = log;
    }

    /// <summary>A page started a download (after its file was chosen). Used to open the downloads flyout.</summary>
    public event EventHandler<long>? DownloadStarted;

    /// <summary>The live operation for this download still exists (so pause/resume/cancel can work).</summary>
    public bool IsLive(long id) => _live.ContainsKey(id);

    /// <summary>Downloads that would stop if AJBrowser closed now: running or paused ones with a live operation.</summary>
    public int UnfinishedCount => _byCore.Values.Sum(set => set.Count);

    /// <summary>This WebView has downloads that need it (closing it would freeze them).</summary>
    public bool OwnsDownloads(CoreWebView2 core) => _byCore.TryGetValue(core, out var set) && set.Count > 0;

    /// <summary>How many closed tabs' WebViews are being kept for their downloads (for the log and tests by hand).</summary>
    public int ParkedCount => _parked.Count;

    public void Attach(CoreWebView2 core) => core.DownloadStarting += OnDownloadStarting;

    public void Detach(CoreWebView2 core) => core.DownloadStarting -= OnDownloadStarting;

    public void Pause(long id) => Run(id, op => { if (op.State == CoreWebView2DownloadState.InProgress) op.Pause(); });

    /// <summary>Continues a download the user paused (always allowed) or one interrupted by an error (only if CanResume).</summary>
    public void Resume(long id) => Run(id, op =>
    {
        if (op.CanResume || (op.State == CoreWebView2DownloadState.Interrupted && op.InterruptReason == CoreWebView2DownloadInterruptReason.UserPaused))
        {
            op.Resume();
        }
    });

    /// <summary>Cancels; the runtime deletes its partial data.</summary>
    public void Cancel(long id) => Run(id, op => op.Cancel());

    /// <summary>
    /// A closing tab's WebView that still owns downloads: AJBrowser keeps it (out of the window, page unloaded) and
    /// disposes it when its downloads are over. The caller has already removed it from the window and its own events.
    /// </summary>
    public void Park(WebView2 webView, CoreWebView2 core)
    {
        _parked[core] = webView;
        try
        {
            core.IsMuted = true;
            core.Navigate("about:blank"); // unloads the page (scripts, video); downloads continue (spike F18)
        }
        catch (Exception ex)
        {
            _log.Debug($"Unloading a parked page failed: {ex.GetType().Name}");
        }

        _log.Info($"Kept a closed tab's engine for its download(s) ({_parked.Count} kept)");
        if (!OwnsDownloads(core))
        {
            ReleaseParked(core); // finished meanwhile
        }
    }

    // ---------------------------------------------------------------- choosing the file

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        var core = (CoreWebView2)sender!;
        var operation = e.DownloadOperation;
        var pageUrl = core.Source;
        e.Handled = true; // no Edge download flyout

        // Claim the WebView right away: a popup that only started a download closes itself straight after this
        // event (F17), and that tab must not dispose its WebView while the download runs.
        var tracked = new Tracked(operation, core);
        Own(tracked);

        string suggested;
        try
        {
            // WebView2 has already worked out a name (Content-Disposition, URL, MIME type); we only make it safe.
            var runtimeName = Path.GetFileName(e.ResultFilePath);
            suggested = !string.IsNullOrWhiteSpace(runtimeName)
                ? FileNamer.Sanitize(runtimeName)
                : FileNamer.Suggest(new Uri(operation.Uri), operation.ContentDisposition, operation.MimeType);
        }
        catch (Exception)
        {
            suggested = FileNamer.FallbackName;
        }

        var folder = ResolveFolder();
        if (!_settings.Current.AskWhereToSave)
        {
            var path = _namer.ReservePath(folder, suggested);
            e.ResultFilePath = path;
            _ = TrackAsync(tracked, pageUrl, path, reserved: true);
            return;
        }

        // "Ask where to save": WebView2 waits for the deferral while our dialog is open. The dialog must not run
        // inside this event handler (it would pump messages within a COM callback), so open it on the next turn.
        var deferral = e.GetDeferral();
        Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            string? chosen = null;
            try
            {
                chosen = _askSavePath(folder, suggested);
                if (chosen is null)
                {
                    e.Cancel = true;
                    _log.Info("Download cancelled in the Save dialog");
                }
                else
                {
                    e.ResultFilePath = chosen; // the dialog already asked before replacing an existing file
                }
            }
            catch (Exception ex)
            {
                _log.Error("The Save dialog failed; the download was cancelled", ex);
                e.Cancel = true;
                chosen = null;
            }
            finally
            {
                deferral.Complete();
            }

            if (chosen is null)
            {
                Finish(tracked);
            }
            else
            {
                _ = TrackAsync(tracked, pageUrl, chosen, reserved: false);
            }
        });
    }

    /// <summary>The folder from Settings if it exists (or can be created), else the Windows Downloads folder.</summary>
    private string ResolveFolder()
    {
        var defaultFolder = _defaultFolder();
        var folder = _settings.Current.DownloadFolder ?? defaultFolder;
        try
        {
            Directory.CreateDirectory(folder);
            return folder;
        }
        catch (Exception ex)
        {
            _log.Warn($"Download folder unavailable ({ex.GetType().Name}); using the Downloads folder instead");
            Directory.CreateDirectory(defaultFolder);
            return defaultFolder;
        }
    }

    // ---------------------------------------------------------------- following the download

    private async Task TrackAsync(Tracked tracked, string? pageUrl, string path, bool reserved)
    {
        // Subscribe first: events that arrive while the database row is being created are caught up below.
        var operation = tracked.Operation;
        operation.StateChanged += (_, _) => Report(tracked);
        operation.BytesReceivedChanged += (_, _) => Report(tracked);

        try
        {
            var item = await _manager.AddBrowserDownloadAsync(operation.Uri, pageUrl, path, operation.TotalBytesToReceive is > 0 and var total ? (long)total : null);
            tracked.Id = item.Id;
            _live[item.Id] = tracked;
            _log.Info($"Download started: {Redactor.Url(operation.Uri)} → {Path.GetFileName(path)}");
            Report(tracked);
            DownloadStarted?.Invoke(this, item.Id);
        }
        catch (Exception ex)
        {
            // The download continues in the runtime; it just won't be in our list.
            _log.Error("Could not record a download", ex);
            if (reserved)
            {
                _namer.Release(path);
            }

            tracked.Unrecorded = true; // still followed, so its WebView is released when it ends
            Report(tracked);
        }
    }

    private void Report(Tracked tracked)
    {
        if ((tracked.Id == 0 && !tracked.Unrecorded) || tracked.IsFinished)
        {
            return; // not registered yet (TrackAsync reports once it is), or already over
        }

        var op = tracked.Operation;
        try
        {
            var reason = op.State == CoreWebView2DownloadState.Interrupted ? op.InterruptReason.ToString() : null;
            var (state, error) = BrowserDownloadStatus.Map(op.State.ToString(), reason);
            long? total = op.TotalBytesToReceive is > 0 and var t ? (long)t : null;
            var canResume = op.CanResume;
            if (!tracked.Unrecorded)
            {
                _manager.UpdateBrowserDownload(tracked.Id, state, op.BytesReceived, total, canResume, error, op.ResultFilePath);
            }

            if (state != tracked.LastState)
            {
                tracked.LastState = state;
                if (state is DownloadState.Completed or DownloadState.Failed or DownloadState.Cancelled)
                {
                    _log.Info($"Download {state.ToString().ToLowerInvariant()}{(reason is null ? string.Empty : $" ({reason})")}: {Path.GetFileName(op.ResultFilePath)}");
                }

                // Over for good: nothing can continue it, so it no longer needs its operation or its WebView.
                // (Failed shows Retry, which starts a new download; an interruption that can't resume is the same.)
                if (state is DownloadState.Completed or DownloadState.Cancelled or DownloadState.Failed
                    || (state == DownloadState.Interrupted && !canResume))
                {
                    Finish(tracked);
                }
            }
        }
        catch (Exception ex)
        {
            // The browser process is gone: the COM object can't be read any more.
            _log.Debug($"Download operation unavailable: {ex.GetType().Name}");
            Finish(tracked);
        }
    }

    private void Run(long id, Action<CoreWebView2DownloadOperation> action)
    {
        if (!_live.TryGetValue(id, out var tracked))
        {
            return;
        }

        try
        {
            action(tracked.Operation);
        }
        catch (Exception ex)
        {
            _log.Warn($"Download action failed: {ex.GetType().Name}");
        }
    }

    // ---------------------------------------------------------------- which WebView owns what

    private void Own(Tracked tracked)
    {
        if (!_byCore.TryGetValue(tracked.Core, out var set))
        {
            _byCore[tracked.Core] = set = [];
        }

        set.Add(tracked);
    }

    /// <summary>The download is over (or never started): forget its operation and free its WebView if it was parked.</summary>
    private void Finish(Tracked tracked)
    {
        if (tracked.IsFinished)
        {
            return;
        }

        tracked.IsFinished = true;
        if (tracked.Id != 0)
        {
            _live.Remove(tracked.Id);
        }

        if (_byCore.TryGetValue(tracked.Core, out var set))
        {
            set.Remove(tracked);
            if (set.Count == 0)
            {
                _byCore.Remove(tracked.Core);
                if (_parked.ContainsKey(tracked.Core))
                {
                    // Not inside the operation's own event: dispose on the next dispatcher turn.
                    var core = tracked.Core;
                    Dispatcher.CurrentDispatcher.BeginInvoke(() => ReleaseParked(core));
                }
            }
        }
    }

    private void ReleaseParked(CoreWebView2 core)
    {
        if (OwnsDownloads(core) || !_parked.Remove(core, out var webView))
        {
            return; // a new download started meanwhile, or already released
        }

        try
        {
            webView.Dispose();
        }
        catch (Exception ex)
        {
            _log.Debug($"Disposing a kept engine failed: {ex.GetType().Name}");
        }

        _log.Info($"Released a closed tab's engine after its downloads ({_parked.Count} still kept)");
    }

    /// <summary>App exit: dispose kept WebViews (their downloads end with the browser process anyway).</summary>
    public void Dispose()
    {
        foreach (var webView in _parked.Values)
        {
            try
            {
                webView.Dispose();
            }
            catch (Exception)
            {
                // Exiting; nothing to do.
            }
        }

        _parked.Clear();
    }

    private sealed class Tracked(CoreWebView2DownloadOperation operation, CoreWebView2 core)
    {
        public CoreWebView2DownloadOperation Operation { get; } = operation;

        /// <summary>The WebView the download belongs to (it must stay alive until the download is over).</summary>
        public CoreWebView2 Core { get; } = core;

        public long Id { get; set; }
        public DownloadState? LastState { get; set; }
        public bool IsFinished { get; set; }

        /// <summary>Saving it to the list failed: not shown, but still followed until it ends.</summary>
        public bool Unrecorded { get; set; }
    }
}
