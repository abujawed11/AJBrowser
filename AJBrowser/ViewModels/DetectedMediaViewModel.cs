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
    private readonly Func<DetectedMediaViewModel, Task> _download;

    public DetectedMediaViewModel(DetectedMedia media, Func<DetectedMediaViewModel, Task> download)
    {
        _download = download;
        Media = media;
    }

    public DetectedMedia Media { get; private set; }

    public int Id => Media.Id;

    public bool IsStream => Media.Kind is MediaKind.Hls or MediaKind.Dash;

    public bool CanDownload => Media.Kind == MediaKind.Direct && !IsStarting;

    /// <summary>"1280×720 · MP4", "Audio · M4A", "Unknown quality · WebM", "Stream · HLS".</summary>
    public string Title
    {
        get
        {
            var quality = IsStream ? "Stream" : Media.Resolution ?? (Media.IsAudioOnly ? "Audio" : "Unknown quality");
            return Media.Container is { } container ? $"{quality} · {container}" : quality;
        }
    }

    /// <summary>"12.3 MB · 1:05 · cdn.example.com", or for streams that downloading them comes in a later version.</summary>
    public string Details
    {
        get
        {
            if (IsStream)
            {
                return $"Stream downloads arrive in a later version · {Media.Host}";
            }

            var parts = new List<string> { Media.TotalBytes is { } bytes ? DownloadText.Size(bytes) : "Unknown size" };
            if (Media.DurationSeconds is { } seconds)
            {
                parts.Add(FormatDuration(seconds));
            }

            parts.Add(Media.Host);
            return string.Join(" · ", parts);
        }
    }

    public string ToolTip => Media.DisplayUrl;

    /// <summary>The download is being queued (cookies are being fetched): the button waits.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    public partial bool IsStarting { get; set; }

    /// <summary>Shows "Added to downloads" after a click, so it's clear it worked.</summary>
    [ObservableProperty]
    public partial bool WasQueued { get; set; }

    public void Update(DetectedMedia media)
    {
        Media = media;
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private Task Download() => _download(this);

    private static string FormatDuration(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Round(seconds));
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
}
