using System.Collections.ObjectModel;
using System.Globalization;
using AJBrowser.Core.Downloads;
using AJBrowser.Core.Media.YtDlp;
using AJBrowser.Core.Tools;
using AJBrowser.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AJBrowser.ViewModels;

/// <summary>
/// The yt-dlp row in the "Videos on this page" picker (plan Phase 7.5c): one video page, read by yt-dlp when the
/// picker opens. Shows the title, length, uploader, a quality list and (several languages, FFmpeg present) a sound
/// list, Download / Save as…, notes, and problems with the button that fixes them (install or update a tool, retry).
/// </summary>
public sealed partial class YtDlpVideoViewModel : ObservableObject
{
    private static readonly YtDlpAudioChoice AutomaticSound = new("Sound: automatic (original language)", string.Empty, null);

    private readonly YtDlpService _service;
    private YtDlpInfo? _info;

    public YtDlpVideoViewModel(Uri pageUrl, YtDlpService service, string? pageTitle)
    {
        PageUrl = pageUrl;
        _service = service;
        Site = YtDlpSites.SiteOf(pageUrl) ?? pageUrl.Host;
        Title = string.IsNullOrWhiteSpace(pageTitle) ? Site : pageTitle;
    }

    /// <summary>The page this row is about (the tab's address when the picker was opened).</summary>
    public Uri PageUrl { get; }

    public string Site { get; }

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Details), nameof(CanDownload))]
    public partial bool IsBusy { get; set; }

    /// <summary>"Reading video info…", or "10:35 · Blender · YouTube".</summary>
    public string Details
    {
        get
        {
            if (IsBusy && _info is null)
            {
                return $"yt-dlp · reading video info… · {Site}";
            }

            var parts = new List<string> { "yt-dlp" };
            if (_info?.DurationSeconds is { } seconds)
            {
                var time = TimeSpan.FromSeconds(Math.Round(seconds));
                parts.Add(time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : time.ToString(@"m\:ss", CultureInfo.InvariantCulture));
            }

            if (_info?.Uploader is { } uploader)
            {
                parts.Add(uploader);
            }

            parts.Add(Site);
            return string.Join(" · ", parts);
        }
    }

    public ObservableCollection<YtDlpQualityRow> Qualities { get; } = [];

    [ObservableProperty]
    public partial YtDlpQualityRow? SelectedQuality { get; set; }

    public bool HasQualities => Qualities.Count > 1;

    public ObservableCollection<YtDlpAudioChoice> AudioTracks { get; } = [];

    [ObservableProperty]
    public partial YtDlpAudioChoice? SelectedAudio { get; set; }

    public bool HasAudioChoices => AudioTracks.Count > 1;

    public bool CanDownload => _info is not null && Qualities.Count > 0 && !IsBusy && !IsStarting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    public partial bool IsStarting { get; set; }

    [ObservableProperty]
    public partial bool WasQueued { get; set; }

    /// <summary>Why nothing can be downloaded (or the download didn't start); empty when fine.</summary>
    [ObservableProperty]
    public partial string Message { get; set; } = string.Empty;

    /// <summary>Information that isn't an error ("install FFmpeg for higher qualities"), or install progress.</summary>
    [ObservableProperty]
    public partial string Note { get; set; } = string.Empty;

    /// <summary>What the action button does; <see cref="YtDlpFix.None"/> hides it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionLabel), nameof(HasAction))]
    public partial YtDlpFix Action { get; set; }

    public bool HasAction => Action is not (YtDlpFix.None or YtDlpFix.SignIn);

    public string ActionLabel => Action switch
    {
        YtDlpFix.UpdateYtDlp => _service.HasYtDlp ? "Update yt-dlp" : "Install yt-dlp",
        YtDlpFix.InstallDeno => "Install Deno",
        YtDlpFix.InstallFfmpeg => "Install FFmpeg",
        _ => "Try again",
    };

    /// <summary>Reads the page with yt-dlp (or reuses a recent answer) and fills the lists.</summary>
    public async Task InspectAsync()
    {
        IsBusy = true;
        Message = string.Empty;
        Note = string.Empty;
        Action = YtDlpFix.None;
        try
        {
            var inspection = await _service.InspectAsync(PageUrl);
            if (inspection.Info is not { } info)
            {
                var problem = inspection.Problem ?? new YtDlpProblem("yt-dlp couldn't read this page.", YtDlpFix.Retry);
                Message = problem.Message;
                Action = problem.Fix;
                return;
            }

            Apply(info);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Apply(YtDlpInfo info)
    {
        _info = info;
        Title = info.Title;
        var choices = _service.Choices(info);
        Qualities.Clear();
        foreach (var row in choices.Rows)
        {
            Qualities.Add(row);
        }

        SelectedQuality = Qualities.FirstOrDefault();
        AudioTracks.Clear();
        if (choices.AudioTracks.Count > 1)
        {
            AudioTracks.Add(AutomaticSound);
            foreach (var track in choices.AudioTracks)
            {
                AudioTracks.Add(track);
            }
        }

        SelectedAudio = AudioTracks.FirstOrDefault();
        Message = choices.Problem ?? string.Empty;
        Note = choices.Note ?? string.Empty;
        Action = !choices.CanDownload && choices.Problem is not null ? YtDlpFix.None
            : !_service.HasFfmpeg && choices.Note is not null ? YtDlpFix.InstallFfmpeg
            : Site == "YouTube" && !_service.HasDeno ? YtDlpFix.InstallDeno
            : YtDlpFix.None;
        if (Action == YtDlpFix.InstallDeno)
        {
            Note = "Some YouTube qualities need the Deno helper (about 43 MB).";
        }

        OnPropertyChanged(nameof(HasQualities));
        OnPropertyChanged(nameof(HasAudioChoices));
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(CanDownload));
    }

    [RelayCommand]
    private Task Download() => StartAsync(saveAs: false);

    [RelayCommand]
    private Task SaveAs() => StartAsync(saveAs: true);

    private async Task StartAsync(bool saveAs)
    {
        if (!CanDownload || _info is null || SelectedQuality is not { } row)
        {
            return;
        }

        if (SelectedAudio is { FormatId.Length: > 0 } sound && row.AudioFormatId is not null && row.VideoFormatId is not null)
        {
            row = YtDlpFormats.WithAudio(row, _info, sound);
        }

        IsStarting = true;
        try
        {
            var outcome = await _service.StartAsync(PageUrl, _info, row, saveAs);
            WasQueued = outcome.Item is not null || WasQueued;
            Message = outcome.Item is null ? outcome.Message ?? string.Empty : string.Empty;
        }
        finally
        {
            IsStarting = false;
        }
    }

    /// <summary>The action button: install or update the missing tool, then read the page again; or just try again.</summary>
    [RelayCommand]
    private async Task Fix()
    {
        var tool = Action switch
        {
            YtDlpFix.UpdateYtDlp => ToolKind.YtDlp,
            YtDlpFix.InstallDeno => ToolKind.Deno,
            YtDlpFix.InstallFfmpeg => ToolKind.Ffmpeg,
            _ => (ToolKind?)null,
        };

        if (tool is { } kind)
        {
            IsBusy = true;
            Message = string.Empty;
            var name = ToolCatalog.For(kind).DisplayName;
            var result = await _service.InstallAsync(kind, new Progress<ToolInstallProgress>(p => Note = p.Stage == "Downloading" && p.TotalBytes is { } total
                ? $"Installing {name}: {DownloadText.Size(p.ReceivedBytes, CultureInfo.CurrentCulture)} of {DownloadText.Size(total, CultureInfo.CurrentCulture)}…"
                : $"Installing {name}…"));
            IsBusy = false;
            if (!result.Succeeded)
            {
                Note = string.Empty;
                Message = result.Error ?? $"Installing {name} failed.";
                return;
            }
        }

        _service.Forget(PageUrl);
        _info = null;
        Qualities.Clear();
        AudioTracks.Clear();
        await InspectAsync();
    }
}
