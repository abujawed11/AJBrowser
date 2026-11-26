using System.Globalization;
using System.IO;
using AJBrowser.Core.Downloads;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AJBrowser.ViewModels;

/// <summary>
/// One row in the downloads flyout and on the Downloads page (both show the same instance, so progress updates
/// once for both). Wraps the latest <see cref="DownloadItem"/> snapshot; the buttons ask <see cref="DownloadsViewModel"/>.
/// </summary>
public sealed partial class DownloadItemViewModel : ObservableObject
{
    private readonly DownloadsViewModel _owner;

    public DownloadItemViewModel(DownloadItem item, DownloadsViewModel owner)
    {
        _owner = owner;
        Item = item;
        RefreshFileState();
    }

    public DownloadItem Item { get; private set; }

    public long Id => Item.Id;

    public string FileName => Item.FileName;

    /// <summary>Where it came from, without query or fragment (signed links carry secrets there).</summary>
    public string DisplayUrl => Item.DisplayUrl;

    public string Host => Uri.TryCreate(Item.SourceUrl, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : DisplayUrl;

    public string StatusText => DownloadText.Status(Item, FileMissing);

    /// <summary>"Today 14:32", "Yesterday 09:10" or the date, for the Downloads page.</summary>
    public string StartedText
    {
        get
        {
            var local = Item.StartedAtUtc.ToLocalTime();
            var day = local.Date == DateTime.Today ? "Today"
                : local.Date == DateTime.Today.AddDays(-1) ? "Yesterday"
                : local.ToString("d", CultureInfo.CurrentCulture);
            return $"{day} {local.ToString("t", CultureInfo.CurrentCulture)}";
        }
    }

    public string ToolTip => $"{FileName}\n{Item.FilePath}\n{DisplayUrl}";

    /// <summary>The download finished but the file isn't there any more (moved or deleted).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(CanOpen))]
    public partial bool FileMissing { get; private set; }

    /// <summary>0–100 for the progress bar.</summary>
    public double Percent => (Item.Fraction ?? 0) * 100;

    public bool IsIndeterminate => Item.State == DownloadState.Downloading && Item.Fraction is null;

    public bool ShowProgress => Item.State is DownloadState.Downloading or DownloadState.Paused or DownloadState.Queued;

    public bool IsProblem => Item.State is DownloadState.Failed or DownloadState.Interrupted;

    public bool IsCompleted => Item.State == DownloadState.Completed;

    public bool CanOpen => IsCompleted && !FileMissing;

    public bool CanPause => Item.CanPause && _owner.CanControl(Item);

    public bool CanResume => Item.CanResumeNow && _owner.CanControl(Item);

    public bool CanCancel => Item.CanCancel && _owner.CanControl(Item);

    /// <summary>Retry starts it again from the beginning; offered only when resuming isn't possible.</summary>
    public bool CanRetry => Item.CanRetry && !CanResume;

    public bool CanRemove => Item.IsFinished;

    /// <summary>A new snapshot arrived from the manager.</summary>
    public void Update(DownloadItem item)
    {
        var stateChanged = item.State != Item.State;
        Item = item;
        if (stateChanged)
        {
            RefreshFileState();
        }

        OnPropertyChanged(string.Empty); // every derived property may have changed
    }

    /// <summary>Checks whether a finished file is still on disk (when the list is shown again).</summary>
    public void RefreshFileState() => FileMissing = Item.State == DownloadState.Completed && !File.Exists(Item.FilePath);

    [RelayCommand]
    private void Open() => _owner.Open(this);

    [RelayCommand]
    private void ShowInFolder() => _owner.ShowInFolder(this);

    [RelayCommand]
    private void Pause() => _owner.Pause(this);

    [RelayCommand]
    private void Resume() => _owner.Resume(this);

    [RelayCommand]
    private void Cancel() => _owner.Cancel(this);

    [RelayCommand]
    private void Retry() => _owner.Retry(this);

    [RelayCommand]
    private void CopyLink() => _owner.CopyLink(this);

    [RelayCommand]
    private Task Remove() => _owner.RemoveAsync(this);
}
