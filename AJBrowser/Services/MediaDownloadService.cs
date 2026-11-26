using System.IO;
using AJBrowser.Core.Downloads;
using AJBrowser.Core.Logging;
using AJBrowser.Core.Media;
using AJBrowser.Core.Security;
using AJBrowser.Core.Settings;

namespace AJBrowser.Services;

/// <summary>
/// Downloads a detected video with AJBrowser's own downloader (an "app download", plan §6.1): a file name from the
/// page title and quality, the folder from Settings, the allowlisted headers the page's request had, and the cookies
/// the tab's profile would send to that exact URL, asked for only now (plan §6.2, §5.8). The download then shows in the
/// download list like any other and can be paused and resumed.
/// </summary>
public sealed class MediaDownloadService
{
    private readonly DownloadManager _manager;
    private readonly FileNamer _namer;
    private readonly SettingsStore _settings;
    private readonly Func<string, string, string?> _askSavePath;
    private readonly AppLogger _log;

    /// <param name="askSavePath">The Save dialog (folder, suggested name) → chosen path or null; shared with browser downloads.</param>
    public MediaDownloadService(DownloadManager manager, FileNamer namer, SettingsStore settings, Func<string, string, string?> askSavePath, AppLogger log)
    {
        _manager = manager;
        _namer = namer;
        _settings = settings;
        _askSavePath = askSavePath;
        _log = log;
    }

    /// <param name="cookieHeader">Asks the tab's profile for the cookies it would send to a URL (null when there are none).</param>
    /// <param name="saveAs">Ask where to save ("Save as…"); also asked when Settings → "Ask where to save" is on.</param>
    public async Task<DownloadItem?> StartAsync(DetectedMedia media, string? pageTitle, Uri? pageUrl, Func<Uri, Task<string?>> cookieHeader, bool saveAs = false)
    {
        if (media.Kind != MediaKind.Direct)
        {
            return null; // streams (HLS/DASH) come in Phases 6 and 7
        }

        var name = MediaFileNamer.Suggest(media, pageTitle);
        var folder = _settings.Current.DownloadFolder ?? KnownFolders.Downloads;
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            _log.Warn($"Download folder unavailable ({ex.GetType().Name}); using the Downloads folder instead");
            folder = KnownFolders.Downloads;
            Directory.CreateDirectory(folder);
        }

        if (saveAs || _settings.Current.AskWhereToSave)
        {
            var chosen = _askSavePath(folder, name);
            if (chosen is null)
            {
                return null; // cancelled in the dialog
            }

            // AJBrowser never overwrites a finished file (it writes .ajpart and renames): an existing name gets " (1)".
            folder = Path.GetDirectoryName(chosen) ?? folder;
            name = Path.GetFileName(chosen);
        }

        var path = _namer.ReservePath(folder, name);

        try
        {
            var cookies = await cookieHeader(media.Url);
            var context = media.Request ?? RequestContext.FromObservedRequest([], pageUrl);
            var item = await _manager.EnqueueAsync(new DownloadRequest(media.Url, path, context.ToHeaders(cookies)), pageUrl?.AbsoluteUri);
            _log.Info($"Video download queued: {media.DisplayUrl} → {Path.GetFileName(path)}{(cookies is null ? string.Empty : " (with cookies)")}");
            return item;
        }
        catch (Exception ex)
        {
            _namer.Release(path);
            _log.Error("Could not start a video download", ex);
            return null;
        }
    }

    /// <summary>
    /// Before Retry/Resume of a video download: if an open tab has seen the same file again (same host and path, usually
    /// with a new token after replaying), switch the download to that fresh address with that tab's headers and cookies.
    /// Also when the address is unchanged: after an app restart the download has lost its headers and cookies (memory only).
    /// </summary>
    public async Task<bool> RefreshSourceAsync(DownloadItem item, Func<string, (DetectedMedia Media, Func<Uri, Task<string?>> Cookies)?> findInTabs)
    {
        if (item.Kind != DownloadKind.App || !Uri.TryCreate(item.SourceUrl, UriKind.Absolute, out var old)
            || findInTabs(MediaClassifier.KeyOf(old)) is not { } found)
        {
            return false;
        }

        var cookies = await found.Cookies(found.Media.Url);
        var headers = (found.Media.Request ?? RequestContext.FromObservedRequest([], null)).ToHeaders(cookies);
        var replaced = _manager.ReplaceSource(item.Id, found.Media.Url, headers);
        if (replaced)
        {
            _log.Info(found.Media.Url.AbsoluteUri == old.AbsoluteUri
                ? $"Video download refreshed its headers and cookies from an open tab: {found.Media.DisplayUrl}"
                : $"Video download got a fresh link from an open tab: {found.Media.DisplayUrl}");
        }

        return replaced;
    }
}
