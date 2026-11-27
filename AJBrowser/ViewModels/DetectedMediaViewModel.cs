using System.Collections.ObjectModel;
using System.Globalization;
using AJBrowser.Core.Downloads;
using AJBrowser.Core.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AJBrowser.ViewModels;

/// <summary>
/// One row in the "Videos on this page" picker. Labels come only from data (plan §6.2 step 6): the resolution the page's
/// player reported, the size the server sent, the format from the content type; otherwise "Unknown …".
/// </summary>
public sealed partial class DetectedMediaViewModel : ObservableObject
{
    private readonly Func<DetectedMediaViewModel, bool, Task> _download;
    private readonly Func<DetectedMedia, string> _name;

    /// <param name="download">Starts the download; the flag asks where to save ("Save as…").</param>
    /// <param name="name">The file name it would be saved as (MediaFileNamer with the page title).</param>
    public DetectedMediaViewModel(DetectedMedia media, Func<DetectedMediaViewModel, bool, Task> download, Func<DetectedMedia, string> name)
    {
        _download = download;
        _name = name;
        Media = media;
    }

    public DetectedMedia Media { get; private set; }

    public int Id => Media.Id;

    public bool IsStream => Media.Kind is MediaKind.Hls or MediaKind.Dash;

    /// <summary>Files and HLS/DASH streams (Phases 6–7) that aren't protected, live or otherwise refused.</summary>
    public bool CanDownload => Media.Kind is MediaKind.Direct or MediaKind.Hls or MediaKind.Dash && !IsStarting && !IsBlocked;

    /// <summary>The stream's playlist said it can't be downloaded (DRM, live): the reason is in <see cref="Message"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    public partial bool IsBlocked { get; set; }

    /// <summary>Streams: the qualities to choose from, best first (empty until the playlist was read, or for a single quality).</summary>
    public ObservableCollection<Services.QualityOption> Qualities { get; } = [];

    [ObservableProperty]
    public partial Services.QualityOption? SelectedQuality { get; set; }

    /// <summary>More than one quality to choose from: the picker shows the list.</summary>
    public bool HasQualities => Qualities.Count > 1;

    /// <summary>DASH: sound tracks to choose from ("automatic" first); empty when there's no choice.</summary>
    public ObservableCollection<Services.AudioOption> AudioTracks { get; } = [];

    [ObservableProperty]
    public partial Services.AudioOption? SelectedAudio { get; set; }

    public bool HasAudioChoices => AudioTracks.Count > 1;

    /// <summary>The stream's playlist is being read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Details))]
    public partial bool IsInspecting { get; set; }

    /// <summary>Length of a stream, once read.</summary>
    private double? _streamDuration;

    /// <summary>Information about the result (for example "sound is a separate track…"), not an error.</summary>
    [ObservableProperty]
    public partial string Note { get; set; } = string.Empty;

    /// <summary>The stream was read once (so it isn't read again when the list refreshes).</summary>
    public bool WasInspected { get; private set; }

    public void ApplyStreamInfo(Services.StreamInfo info)
    {
        WasInspected = true;
        _streamDuration = info.DurationSeconds;
        Qualities.Clear();
        foreach (var quality in info.Qualities)
        {
            Qualities.Add(quality);
        }

        SelectedQuality = Qualities.FirstOrDefault();
        AudioTracks.Clear();
        foreach (var track in info.AudioTracks ?? [])
        {
            AudioTracks.Add(track);
        }

        SelectedAudio = AudioTracks.FirstOrDefault();
        IsBlocked = !info.CanDownload;
        Message = info.Problem ?? string.Empty;
        Note = info.Note ?? string.Empty;
        OnPropertyChanged(nameof(HasQualities));
        OnPropertyChanged(nameof(HasAudioChoices));
        OnPropertyChanged(nameof(Details));
    }

    /// <summary>The file's name: the server's, the one in the address, or the page title (see MediaFileNamer).</summary>
    public string Title => Media.Kind switch
    {
        // A stream's file type (.mp4, .webm, .ts…) is only known once its playlist is read, so no extension here.
        MediaKind.Hls or MediaKind.Dash => System.IO.Path.GetFileNameWithoutExtension(_name(Media)),
        _ => _name(Media),
    };

    /// <summary>"1280×720 · MP4", "Audio · M4A", "Unknown quality · WebM".</summary>
    public string Quality
    {
        get
        {
            var quality = Media.Resolution ?? (Media.IsAudioOnly ? "Audio" : "Unknown quality");
            return Media.Container is { } container ? $"{quality} · {container}" : quality;
        }
    }

    /// <summary>"1920×1080 · MKV · 951 MB · 36:04 · cdn.example.com", or for streams that they come in a later version.</summary>
    public string Details
    {
        get
        {
            if (IsStream)
            {
                var kind = Media.Kind == MediaKind.Hls ? "Stream (HLS)" : "Stream (DASH)";
                return IsInspecting ? $"{kind} · reading qualities… · {Media.Host}"
                    : _streamDuration is { } length ? $"{kind} · {FormatDuration(length)} · {Media.Host}"
                    : $"{kind} · {Media.Host}";
            }

            var parts = new List<string> { Quality, Media.TotalBytes is { } bytes ? DownloadText.Size(bytes) : "Unknown size" };
            if (Media.DurationSeconds is { } seconds)
            {
                parts.Add(FormatDuration(seconds));
            }

            parts.Add(Media.Host);
            return string.Join(" · ", parts);
        }
    }

    public string ToolTip => IsStream ? Media.DisplayUrl : $"{Title}\n{Media.DisplayUrl}";

    /// <summary>The download is being queued (cookies are being fetched): the button waits.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    public partial bool IsStarting { get; set; }

    /// <summary>Shows "Added to downloads" after a click, so it's clear it worked.</summary>
    [ObservableProperty]
    public partial bool WasQueued { get; set; }

    /// <summary>Why it couldn't be downloaded (DRM, live, unreadable playlist) or a note about the result; empty if none.</summary>
    [ObservableProperty]
    public partial string Message { get; set; } = string.Empty;

    public void Update(DetectedMedia media)
    {
        Media = media;
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private Task Download() => _download(this, false);

    [RelayCommand]
    private Task SaveAs() => _download(this, true);

    private static string FormatDuration(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Round(seconds));
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
}
