using System.Globalization;
using AJBrowser.Core.Media.YtDlp;
using AJBrowser.Core.Settings;
using AJBrowser.Core.Tools;

namespace AJBrowser.Services;

/// <summary>
/// What the picker needs for yt-dlp (plan Phase 7.5c): whether it's switched on, reading a page, the quality rows,
/// starting a download, and installing a missing tool from the picker. Holds no per-page state (the row does).
/// </summary>
public sealed class YtDlpService
{
    private readonly YtDlpDownloader _downloader;
    private readonly ToolService _tools;
    private readonly MediaDownloadService _media;
    private readonly SettingsStore _settings;

    public YtDlpService(YtDlpDownloader downloader, ToolService tools, MediaDownloadService media, SettingsStore settings)
    {
        _downloader = downloader;
        _tools = tools;
        _media = media;
        _settings = settings;
        _settings.Changed += (_, previous) =>
        {
            if (previous.YtDlpEnabled != _settings.Current.YtDlpEnabled)
            {
                EnabledChanged?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    /// <summary>Settings → Downloads → "Big video sites (yt-dlp)".</summary>
    public bool IsEnabled => _settings.Current.YtDlpEnabled;

    public event EventHandler? EnabledChanged;

    public bool HasYtDlp => _tools.Locate(ToolKind.YtDlp) is not null;
    public bool HasFfmpeg => _tools.Locate(ToolKind.Ffmpeg) is not null;
    public bool HasDeno => _tools.Locate(ToolKind.Deno) is not null;

    public Task<YtDlpInspection> InspectAsync(Uri page) => _downloader.InspectAsync(page);

    public void Forget(Uri page) => _downloader.Forget(page);

    /// <summary>Quality rows for the current tools (FFmpeg decides whether picture + sound can be joined) and the UI language.</summary>
    public YtDlpChoices Choices(YtDlpInfo info) => YtDlpFormats.Build(info, HasFfmpeg, CultureInfo.CurrentUICulture.Name);

    public Task<MediaDownloadOutcome> StartAsync(Uri page, YtDlpInfo info, YtDlpQualityRow row, bool saveAs) =>
        _media.StartYtDlpAsync(page, info.Title, row, saveAs);

    public Task<ToolInstallResult> InstallAsync(ToolKind kind, IProgress<ToolInstallProgress>? progress) => _tools.InstallAsync(kind, progress);
}
