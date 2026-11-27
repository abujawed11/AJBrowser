using System.IO;
using AJBrowser.Core.Downloads;
using AJBrowser.Core.Logging;
using AJBrowser.Core.Media;
using AJBrowser.Core.Media.Dash;
using AJBrowser.Core.Media.Hls;
using AJBrowser.Core.Security;
using AJBrowser.Core.Settings;

namespace AJBrowser.Services;

/// <summary>
/// One choice in a stream's quality list: "720p · 3 Mbps · H.264/AAC · ≈ 225 MB est.". HLS: <see cref="VariantUri"/>;
/// DASH: <see cref="TrackId"/> (the picture track's <c>Representation@id</c>).
/// </summary>
/// <remarks>HLS also keeps the variant's <see cref="AudioGroup"/> and <see cref="Bandwidth"/>: qualities may share one picture playlist.</remarks>
public sealed record QualityOption(string Label, Uri? VariantUri, string? TrackId = null, string? AudioGroup = null, long? Bandwidth = null)
{
    /// <summary>The label: what screen readers announce for the list item (a record would otherwise read out all its fields).</summary>
    public override string ToString() => Label;
}

/// <summary>One choice in a DASH stream's sound list; <see cref="TrackId"/> null = automatic (language, then codec fit).</summary>
public sealed record AudioOption(string Label, string? TrackId)
{
    public override string ToString() => Label;
}

/// <summary>What the picker shows for a stream once its playlist or manifest was read.</summary>
/// <param name="AudioTracks">DASH: sound tracks to choose from ("automatic" first); empty when there's no choice to make.</param>
public sealed record StreamInfo(
    bool CanDownload,
    string? Problem,
    IReadOnlyList<QualityOption> Qualities,
    double? DurationSeconds,
    string? Note,
    IReadOnlyList<Uri> ReferencedPlaylists,
    IReadOnlyList<AudioOption>? AudioTracks = null);

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
    private readonly DashDownloader _dash;
    private readonly FileNamer _namer;
    private readonly SettingsStore _settings;
    private readonly Func<string?> _ffmpeg;
    private readonly Func<string, string, string?> _askSavePath;
    private readonly AppLogger _log;

    /// <param name="askSavePath">The Save dialog (folder, suggested name) → chosen path or null; shared with browser downloads.</param>
    public MediaDownloadService(DownloadManager manager, HlsDownloader hls, DashDownloader dash, FileNamer namer, SettingsStore settings, Func<string?> ffmpeg, Func<string, string, string?> askSavePath, AppLogger log)
    {
        _manager = manager;
        _hls = hls;
        _dash = dash;
        _ffmpeg = ffmpeg;
        _namer = namer;
        _settings = settings;
        _askSavePath = askSavePath;
        _log = log;
    }

    /// <param name="cookieHeader">Asks the tab's profile for the cookies it would send to a URL (null when there are none).</param>
    /// <param name="saveAs">Ask where to save ("Save as…"); also asked when Settings → "Ask where to save" is on.</param>
    /// <param name="quality">Streams: the chosen quality (null = the best).</param>
    /// <param name="audio">DASH: the chosen sound track (null or "automatic" = picked by language and codec).</param>
    public async Task<MediaDownloadOutcome> StartAsync(
        DetectedMedia media, string? pageTitle, Uri? pageUrl, Func<Uri, Task<string?>> cookieHeader, bool saveAs = false, QualityOption? quality = null, AudioOption? audio = null)
    {
        switch (media.Kind)
        {
            case MediaKind.Hls:
                return await StartHlsAsync(media, pageTitle, pageUrl, cookieHeader, saveAs, quality);
            case MediaKind.Dash:
                return await StartDashAsync(media, pageTitle, pageUrl, cookieHeader, saveAs, quality?.TrackId, audio?.TrackId);
            case MediaKind.Direct:
                break;
            default:
                return new MediaDownloadOutcome(null, "This can't be downloaded.");
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
    private async Task<MediaDownloadOutcome> StartHlsAsync(DetectedMedia media, string? pageTitle, Uri? pageUrl, Func<Uri, Task<string?>> cookieHeader, bool saveAs, QualityOption? quality)
    {
        var context = media.Request ?? RequestContext.FromObservedRequest([], pageUrl);
        HlsPlan plan;
        try
        {
            plan = await _hls.PrepareAsync(media.Url, context.ToHeaders(await cookieHeader(media.Url)), quality?.VariantUri,
                audioGroup: quality?.AudioGroup, bandwidth: quality?.Bandwidth);
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
                new DownloadRequest(plan.MediaPlaylistUrl!, path, context.ToHeaders(cookies), DownloadSource.Hls, plan.AudioPlaylistUrl, ExpectedBytes: plan.EstimatedBytes),
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

    /// <summary>
    /// A DASH stream (Phase 7): read the manifest with the page's headers and cookies, refuse DRM/live/multi-part videos,
    /// choose the picture track (the chosen or best quality) and the sound track (chosen, or by language and codec; only
    /// when FFmpeg can merge it), name the file after the result's type and queue the job; the download manager runs it
    /// with <see cref="DashDownloader"/>.
    /// </summary>
    private async Task<MediaDownloadOutcome> StartDashAsync(
        DetectedMedia media, string? pageTitle, Uri? pageUrl, Func<Uri, Task<string?>> cookieHeader, bool saveAs, string? videoId, string? audioId)
    {
        var context = media.Request ?? RequestContext.FromObservedRequest([], pageUrl);
        DashPlan plan;
        try
        {
            plan = await _dash.PrepareAsync(media.Url, context.ToHeaders(await cookieHeader(media.Url)), videoId, audioId, PreferredLanguage);
        }
        catch (Exception ex)
        {
            _log.Warn($"Reading a DASH manifest failed: {ex.GetType().Name}");
            return new MediaDownloadOutcome(null, "The stream's manifest couldn't be read.");
        }

        if (!plan.CanDownload)
        {
            return new MediaDownloadOutcome(null, plan.Problem ?? "This stream can't be downloaded.");
        }

        var named = media with { Height = plan.Video!.Height ?? media.Height, IsAudioOnly = false };
        var name = Path.ChangeExtension(MediaFileNamer.Suggest(named, pageTitle), plan.OutputExtension);
        var folder = DownloadFolder();
        if (!ChooseLocation(ref folder, ref name, saveAs))
        {
            return MediaDownloadOutcome.Cancelled;
        }

        var path = _namer.ReservePath(folder, name);
        try
        {
            // Cookies for the manifest's own host; the downloader sends them only to that host.
            var cookies = await cookieHeader(media.Url);
            var item = await _manager.EnqueueAsync(
                new DownloadRequest(media.Url, path, context.ToHeaders(cookies), DownloadSource.Dash, null, plan.Video.Id, plan.Audio?.Id, plan.EstimatedBytes),
                pageUrl?.AbsoluteUri);
            var height = plan.Video.Height?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?";
            var sound = plan.Audio is null ? "no separate sound" : $"sound \"{DashAnalysis.AudioLabel(plan.Audio)}\"";
            _log.Info($"DASH download queued: {media.DisplayUrl} (picture {height}p, {sound}) → {Path.GetFileName(path)}");
            var note = plan.HasSeparateAudio && plan.Audio is null ? NoSoundNote : null;
            return new MediaDownloadOutcome(item, note);
        }
        catch (Exception ex)
        {
            _namer.Release(path);
            _log.Error("Could not start a DASH download", ex);
            return new MediaDownloadOutcome(null, "Couldn't start the download (details in the log).");
        }
    }

    /// <summary>
    /// A video through yt-dlp (Phase 7.5c): file name from the video's title and quality with the row's real type,
    /// the folder from Settings (or Save as…), and a <see cref="DownloadSource.YtDlp"/> job for the page with the
    /// chosen format ids. No headers or cookies are stored: yt-dlp gets the browser's cookies when each run starts.
    /// </summary>
    public async Task<MediaDownloadOutcome> StartYtDlpAsync(Uri page, string title, Core.Media.YtDlp.YtDlpQualityRow row, bool saveAs)
    {
        var stem = FileNamer.Sanitize(title.Length > 150 ? title[..150] : title);
        var name = row.IsAudioOnly || row.Height is null ? $"{stem}.{row.OutputExtension}" : $"{stem} {row.Height}p.{row.OutputExtension}";
        var folder = DownloadFolder();
        if (!ChooseLocation(ref folder, ref name, saveAs))
        {
            return MediaDownloadOutcome.Cancelled;
        }

        var path = _namer.ReservePath(folder, name);
        try
        {
            var item = await _manager.EnqueueAsync(
                new DownloadRequest(page, path, null, DownloadSource.YtDlp, null, row.VideoFormatId, row.AudioFormatId, row.EstimatedBytes, row.PartBytes),
                page.AbsoluteUri);
            _log.Info($"yt-dlp download queued: {Redactor.Url(page)} (formats {row.FormatSelector}) → {Path.GetFileName(path)}");
            return new MediaDownloadOutcome(item, null);
        }
        catch (Exception ex)
        {
            _namer.Release(path);
            _log.Error("Could not start a yt-dlp download", ex);
            return new MediaDownloadOutcome(null, "Couldn't start the download (details in the log).");
        }
    }

    /// <summary>The user's language for choosing a sound track ("en-IN" matches English tracks).</summary>
    private static string PreferredLanguage => System.Globalization.CultureInfo.CurrentUICulture.Name;

    private const string MergeNote = "Picture and sound are separate in this stream; they'll be merged into one file.";

    private const string NoSoundNote = "The sound is a separate track: choose FFmpeg in Settings → Downloads to include it (without it, picture only).";

    /// <summary>
    /// Reads a stream for the picker: qualities with labels ("720p · 3 Mbps · H.264/AAC · ≈ 225 MB est."), whether it can
    /// be downloaded, and the playlists the master refers to. Uses the page's headers and cookies; never throws.
    /// </summary>
    public async Task<StreamInfo> InspectAsync(DetectedMedia media, Uri? pageUrl, Func<Uri, Task<string?>> cookieHeader)
    {
        if (media.Kind == MediaKind.Dash)
        {
            return await InspectDashAsync(media, pageUrl, cookieHeader);
        }

        try
        {
            var context = media.Request ?? RequestContext.FromObservedRequest([], pageUrl);
            var inspection = await _hls.InspectAsync(media.Url, context.ToHeaders(await cookieHeader(media.Url)));
            var qualities = inspection.Variants
                .Select(v => new QualityOption(HlsAnalysis.VariantLabel(v, inspection.DurationSeconds), v.Uri, null, v.AudioGroup, v.Bandwidth))
                .ToList();
            var canDownload = inspection.Support == HlsSupport.Supported;
            var note = canDownload && inspection.HasSeparateAudio && _ffmpeg() is null ? NoSoundNote : null;
            return new StreamInfo(canDownload, inspection.Problem, qualities, inspection.DurationSeconds, note, inspection.ReferencedPlaylists);
        }
        catch (Exception ex)
        {
            _log.Debug($"Reading a stream for the picker failed: {ex.GetType().Name}");
            return new StreamInfo(true, null, [], null, null, []); // let Download try (it reports problems itself)
        }
    }

    /// <summary>
    /// A DASH manifest for the picker: one quality per picture track (the size estimate includes the sound that would be
    /// merged with it), and, when FFmpeg can merge sound and there's more than one track, the sound choices.
    /// </summary>
    private async Task<StreamInfo> InspectDashAsync(DetectedMedia media, Uri? pageUrl, Func<Uri, Task<string?>> cookieHeader)
    {
        try
        {
            var context = media.Request ?? RequestContext.FromObservedRequest([], pageUrl);
            var inspection = await _dash.InspectAsync(media.Url, context.ToHeaders(await cookieHeader(media.Url)));
            if (inspection.Support != DashSupport.Supported || inspection.Manifest is not { } manifest)
            {
                return new StreamInfo(false, inspection.Problem, [], inspection.DurationSeconds, null, inspection.ReferencedFiles);
            }

            var ffmpeg = _ffmpeg() is not null;
            var qualities = inspection.Video.Select(v =>
            {
                var sound = ffmpeg ? DashAnalysis.PickAudio(manifest, v, PreferredLanguage) : null;
                return new QualityOption(DashAnalysis.VideoLabel(v, inspection.DurationSeconds, sound), null, v.Id);
            }).ToList();

            IReadOnlyList<AudioOption> audio = ffmpeg && inspection.HasSeparateAudio && inspection.Audio.Count > 1
                ? [new AudioOption("Sound: automatic (your language)", null), .. inspection.Audio.Select(a => new AudioOption("Sound: " + DashAnalysis.AudioLabel(a), a.Id))]
                : [];
            var note = !inspection.HasSeparateAudio ? null : ffmpeg ? MergeNote : NoSoundNote;
            return new StreamInfo(true, null, qualities, inspection.DurationSeconds, note, inspection.ReferencedFiles, audio);
        }
        catch (Exception ex)
        {
            _log.Debug($"Reading a DASH manifest for the picker failed: {ex.GetType().Name}");
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
