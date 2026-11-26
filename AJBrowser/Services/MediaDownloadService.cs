using System.IO;
using AJBrowser.Core.Downloads;
using AJBrowser.Core.Logging;
using AJBrowser.Core.Media;
using AJBrowser.Core.Media.Hls;
using AJBrowser.Core.Security;
using AJBrowser.Core.Settings;

namespace AJBrowser.Services;

/// <summary>One choice in a stream's quality list: "720p · 3 Mbps · H.264/AAC · ≈ 225 MB est.".</summary>
public sealed record QualityOption(string Label, Uri? VariantUri);

/// <summary>What the picker shows for a stream once its playlist was read.</summary>
public sealed record StreamInfo(bool CanDownload, string? Problem, IReadOnlyList<QualityOption> Qualities, double? DurationSeconds, string? Note, IReadOnlyList<Uri> ReferencedPlaylists);

/// <summary>What happened when a video download was asked for: the queued item, or why not (shown in the picker).</summary>
public sealed record MediaDownloadOutcome(DownloadItem? Item, string? Message)
{
    public static readonly MediaDownloadOutcome Cancelled = new(null, null);
}

/// <summary>
/// Downloads a detected video with AJBrowser's own downloader (an "app download", plan §6.1): a file name from the
/// page title and quality, the folder from Settings, the allowlisted headers the page's request had, and the cookies
/// the tab's profile would send to that exact URL, asked for only now (plan §6.2, §5.8). The download then shows in the
/// download list like any other and can be paused and resumed.
/// </summary>
public sealed class MediaDownloadService
{
    private readonly DownloadManager _manager;
    private readonly HlsDownloader _hls;
    private readonly FileNamer _namer;
    private readonly SettingsStore _settings;
    private readonly Func<string, string, string?> _askSavePath;
    private readonly AppLogger _log;

    /// <param name="askSavePath">The Save dialog (folder, suggested name) → chosen path or null; shared with browser downloads.</param>
    public MediaDownloadService(DownloadManager manager, HlsDownloader hls, FileNamer namer, SettingsStore settings, Func<string, string, string?> askSavePath, AppLogger log)
    {
        _manager = manager;
        _hls = hls;
        _namer = namer;
        _settings = settings;
        _askSavePath = askSavePath;
        _log = log;
    }

    /// <param name="cookieHeader">Asks the tab's profile for the cookies it would send to a URL (null when there are none).</param>
    /// <param name="saveAs">Ask where to save ("Save as…"); also asked when Settings → "Ask where to save" is on.</param>
    /// <param name="variantUri">HLS: the chosen quality (null = the best).</param>
    public async Task<MediaDownloadOutcome> StartAsync(DetectedMedia media, string? pageTitle, Uri? pageUrl, Func<Uri, Task<string?>> cookieHeader, bool saveAs = false, Uri? variantUri = null)
    {
        if (media.Kind == MediaKind.Hls)
        {
            return await StartHlsAsync(media, pageTitle, pageUrl, cookieHeader, saveAs, variantUri);
        }

        if (media.Kind != MediaKind.Direct)
        {
            return new MediaDownloadOutcome(null, "DASH streams arrive in a later version."); // Phase 7
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

        if (!ChooseLocation(ref folder, ref name, saveAs))
        {
            return MediaDownloadOutcome.Cancelled;
        }

        var path = _namer.ReservePath(folder, name);

        try
        {
            var cookies = await cookieHeader(media.Url);
            var context = media.Request ?? RequestContext.FromObservedRequest([], pageUrl);
            var item = await _manager.EnqueueAsync(new DownloadRequest(media.Url, path, context.ToHeaders(cookies)), pageUrl?.AbsoluteUri);
            _log.Info($"Video download queued: {media.DisplayUrl} → {Path.GetFileName(path)}{(cookies is null ? string.Empty : " (with cookies)")}");
            return new MediaDownloadOutcome(item, null);
        }
        catch (Exception ex)
        {
            _namer.Release(path);
            _log.Error("Could not start a video download", ex);
            return new MediaDownloadOutcome(null, "Couldn't start the download (details in the log).");
        }
    }

    /// <summary>
    /// An HLS stream: read its playlist(s) with the page's headers and cookies, refuse DRM and live streams with a
    /// clear message, pick the best quality (6c lets you choose), name the file after the result's real format
    /// (.mp4, or .ts without FFmpeg) and queue the job; the download manager runs it with <see cref="HlsDownloader"/>.
    /// </summary>
    private async Task<MediaDownloadOutcome> StartHlsAsync(DetectedMedia media, string? pageTitle, Uri? pageUrl, Func<Uri, Task<string?>> cookieHeader, bool saveAs, Uri? variantUri)
    {
        var context = media.Request ?? RequestContext.FromObservedRequest([], pageUrl);
        HlsPlan plan;
        try
        {
            plan = await _hls.PrepareAsync(media.Url, context.ToHeaders(await cookieHeader(media.Url)), variantUri);
        }
        catch (Exception ex)
        {
            _log.Warn($"Reading a stream's playlist failed: {ex.GetType().Name}");
            return new MediaDownloadOutcome(null, "The stream's playlist couldn't be read.");
        }

        if (!plan.CanDownload)
        {
            return new MediaDownloadOutcome(null, plan.Problem ?? "This stream can't be downloaded.");
        }

        var named = media with { Height = plan.Variant?.Height ?? media.Height, IsAudioOnly = false };
        var name = Path.ChangeExtension(MediaFileNamer.Suggest(named, pageTitle), plan.OutputExtension);
        var folder = DownloadFolder();
        if (!ChooseLocation(ref folder, ref name, saveAs))
        {
            return MediaDownloadOutcome.Cancelled;
        }

        var path = _namer.ReservePath(folder, name);
        try
        {
            // Cookies for the media playlist's own host; the downloader sends them only to that host.
            var cookies = await cookieHeader(plan.MediaPlaylistUrl!);
            var item = await _manager.EnqueueAsync(
                new DownloadRequest(plan.MediaPlaylistUrl!, path, context.ToHeaders(cookies), DownloadSource.Hls, plan.AudioPlaylistUrl),
                pageUrl?.AbsoluteUri);
            _log.Info($"Stream download queued: {Redactor.MediaUrl(plan.MediaPlaylistUrl)} ({plan.Media!.Segments.Count} pieces"
                      + (plan.AudioPlaylistUrl is null ? string.Empty : $", sound track \"{plan.AudioName}\"") + $") → {Path.GetFileName(path)}");
            var note = plan.HasSeparateAudio && plan.AudioPlaylistUrl is null ? NoSoundNote : null;
            return new MediaDownloadOutcome(item, note);
        }
        catch (Exception ex)
        {
            _namer.Release(path);
            _log.Error("Could not start a stream download", ex);
            return new MediaDownloadOutcome(null, "Couldn't start the download (details in the log).");
        }
    }

    private const string NoSoundNote = "The sound is a separate track: choose FFmpeg in Settings → Downloads to include it (without it, picture only).";

    /// <summary>
    /// Reads a stream for the picker: qualities with labels ("720p · 3 Mbps · H.264/AAC · ≈ 225 MB est."), whether it can
    /// be downloaded, and the playlists the master refers to. Uses the page's headers and cookies; never throws.
    /// </summary>
    public async Task<StreamInfo> InspectAsync(DetectedMedia media, Uri? pageUrl, Func<Uri, Task<string?>> cookieHeader)
    {
        try
        {
            var context = media.Request ?? RequestContext.FromObservedRequest([], pageUrl);
            var inspection = await _hls.InspectAsync(media.Url, context.ToHeaders(await cookieHeader(media.Url)));
            var qualities = inspection.Variants
                .Select(v => new QualityOption(HlsAnalysis.VariantLabel(v, inspection.DurationSeconds), v.Uri))
                .ToList();
            var canDownload = inspection.Support == HlsSupport.Supported;
            var note = canDownload && inspection.HasSeparateAudio && Core.Media.Ffmpeg.FfmpegLocator.Find(_settings.Current.FfmpegPath) is null ? NoSoundNote : null;
            return new StreamInfo(canDownload, inspection.Problem, qualities, inspection.DurationSeconds, note, inspection.ReferencedPlaylists);
        }
        catch (Exception ex)
        {
            _log.Debug($"Reading a stream for the picker failed: {ex.GetType().Name}");
            return new StreamInfo(true, null, [], null, null, []); // let Download try (it reports problems itself)
        }
    }

    private string DownloadFolder()
    {
        var folder = _settings.Current.DownloadFolder ?? KnownFolders.Downloads;
        try
        {
            Directory.CreateDirectory(folder);
            return folder;
        }
        catch (Exception)
        {
            Directory.CreateDirectory(KnownFolders.Downloads);
            return KnownFolders.Downloads;
        }
    }

    /// <summary>"Save as…" or Settings → "Ask where to save": the dialog decides. False when it was cancelled.</summary>
    private bool ChooseLocation(ref string folder, ref string name, bool saveAs)
    {
        if (!saveAs && !_settings.Current.AskWhereToSave)
        {
            return true;
        }

        var chosen = _askSavePath(folder, name);
        if (chosen is null)
        {
            return false;
        }

        // AJBrowser never overwrites a finished file (it writes .ajpart and renames): an existing name gets " (1)".
        folder = Path.GetDirectoryName(chosen) ?? folder;
        name = Path.GetFileName(chosen);
        return true;
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
