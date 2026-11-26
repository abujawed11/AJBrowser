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
    private readonly AppLogger _log;

    public MediaDownloadService(DownloadManager manager, FileNamer namer, SettingsStore settings, AppLogger log)
    {
        _manager = manager;
        _namer = namer;
        _settings = settings;
        _log = log;
    }

    /// <param name="cookieHeader">Asks the tab's profile for the cookies it would send to a URL (null when there are none).</param>
    public async Task<DownloadItem?> StartAsync(DetectedMedia media, string? pageTitle, Uri? pageUrl, Func<Uri, Task<string?>> cookieHeader)
    {
        if (media.Kind != MediaKind.Direct)
        {
            return null; // streams (HLS/DASH) come in Phases 6 and 7
        }

        var folder = _settings.Current.DownloadFolder ?? KnownFolders.Downloads;
        string path;
        try
        {
            Directory.CreateDirectory(folder);
            path = _namer.ReservePath(folder, MediaFileNamer.Suggest(media, pageTitle));
        }
        catch (Exception ex)
        {
            _log.Warn($"Download folder unavailable ({ex.GetType().Name}); using the Downloads folder instead");
            folder = KnownFolders.Downloads;
            Directory.CreateDirectory(folder);
            path = _namer.ReservePath(folder, MediaFileNamer.Suggest(media, pageTitle));
        }

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
}
