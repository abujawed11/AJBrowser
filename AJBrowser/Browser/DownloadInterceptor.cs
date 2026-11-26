using System.IO;
using System.Windows.Threading;
using AJBrowser.Core.Downloads;
using AJBrowser.Core.Logging;
using AJBrowser.Core.Security;
using AJBrowser.Core.Settings;
using Microsoft.Web.WebView2.Core;

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
/// </list>
/// One instance for the whole app; every WebView is attached in <see cref="BrowserTab"/>'s Configure.
/// Runs on the UI thread (all WebView2 events do).
/// </summary>
public sealed class DownloadInterceptor
{
    private readonly DownloadManager _manager;
    private readonly FileNamer _namer;
    private readonly SettingsStore _settings;
    private readonly Func<string, string, string?> _askSavePath;
    private readonly Func<string> _defaultFolder;
    private readonly AppLogger _log;
    private readonly Dictionary<long, Tracked> _live = [];

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

    public void Attach(CoreWebView2 core) => core.DownloadStarting += OnDownloadStarting;

    public void Detach(CoreWebView2 core) => core.DownloadStarting -= OnDownloadStarting;

    public void Pause(long id) => Run(id, op => { if (op.State == CoreWebView2DownloadState.InProgress) op.Pause(); });

    public void Resume(long id) => Run(id, op => { if (op.CanResume) op.Resume(); });

    /// <summary>Cancels; the runtime deletes its partial data.</summary>
    public void Cancel(long id) => Run(id, op => op.Cancel());

    // ---------------------------------------------------------------- choosing the file

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        var operation = e.DownloadOperation;
        var pageUrl = (sender as CoreWebView2)?.Source;
        e.Handled = true; // no Edge download flyout

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
            _ = TrackAsync(operation, pageUrl, path, reserved: true);
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

            if (chosen is not null)
            {
                _ = TrackAsync(operation, pageUrl, chosen, reserved: false);
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

    private async Task TrackAsync(CoreWebView2DownloadOperation operation, string? pageUrl, string path, bool reserved)
    {
        // Subscribe first: events that arrive while the database row is being created are caught up below.
        var tracked = new Tracked(operation);
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
        }
    }

    private void Report(Tracked tracked)
    {
        if (tracked.Id == 0)
        {
            return; // not registered yet; TrackAsync reports the current state once it is
        }

        var op = tracked.Operation;
        try
        {
            var reason = op.State == CoreWebView2DownloadState.Interrupted ? op.InterruptReason.ToString() : null;
            var (state, error) = BrowserDownloadStatus.Map(op.State.ToString(), reason);
            long? total = op.TotalBytesToReceive is > 0 and var t ? (long)t : null;
            _manager.UpdateBrowserDownload(tracked.Id, state, op.BytesReceived, total, op.CanResume, error, op.ResultFilePath);

            if (state != tracked.LastState)
            {
                tracked.LastState = state;
                if (state is DownloadState.Completed or DownloadState.Failed or DownloadState.Cancelled)
                {
                    _log.Info($"Download {state.ToString().ToLowerInvariant()}{(reason is null ? string.Empty : $" ({reason})")}: {Path.GetFileName(op.ResultFilePath)}");
                }

                // Nothing more will happen to a finished download that can't be resumed.
                if (state is DownloadState.Completed or DownloadState.Cancelled || (state is DownloadState.Failed && !op.CanResume))
                {
                    _live.Remove(tracked.Id);
                }
            }
        }
        catch (Exception ex)
        {
            // The browser process is gone: the COM object can't be read any more.
            _log.Debug($"Download operation unavailable: {ex.GetType().Name}");
            _live.Remove(tracked.Id);
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

    private sealed class Tracked(CoreWebView2DownloadOperation operation)
    {
        public CoreWebView2DownloadOperation Operation { get; } = operation;
        public long Id { get; set; }
        public DownloadState? LastState { get; set; }
    }
}
