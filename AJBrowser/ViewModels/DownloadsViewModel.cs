using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using AJBrowser.Browser;
using AJBrowser.Core.Downloads;
using AJBrowser.Core.Logging;
using AJBrowser.Core.Settings;
using AJBrowser.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AJBrowser.ViewModels;

/// <summary>
/// The download list for the UI: the toolbar button (with its progress ring), its flyout and the Downloads page all
/// bind to this one object. Mirrors <see cref="DownloadManager"/> (whose events arrive on the UI thread) and sends
/// each action to the right place: app downloads to the manager, browser downloads to <see cref="DownloadInterceptor"/>.
/// </summary>
public sealed partial class DownloadsViewModel : ObservableObject, IDisposable
{
    /// <summary>How many downloads the flyout shows (the page shows all).</summary>
    public const int FlyoutCount = 5;

    private readonly DownloadManager _manager;
    private readonly DownloadInterceptor _interceptor;
    private readonly ShellService _shell;
    private readonly SettingsStore _settings;
    private readonly AppLogger _log;
    private readonly Dictionary<long, DownloadItemViewModel> _byId = [];

    public DownloadsViewModel(DownloadManager manager, DownloadInterceptor interceptor, ShellService shell, SettingsStore settings, AppLogger log)
    {
        _manager = manager;
        _interceptor = interceptor;
        _shell = shell;
        _settings = settings;
        _log = log;
        _manager.ItemChanged += OnItemChanged;
        _manager.ItemRemoved += OnItemRemoved;
        _interceptor.DownloadStarted += OnDownloadStarted;
    }

    /// <summary>All downloads, newest first.</summary>
    public ObservableCollection<DownloadItemViewModel> Items { get; } = [];

    /// <summary>The newest few, for the flyout.</summary>
    public ObservableCollection<DownloadItemViewModel> Recent { get; } = [];

    public bool IsEmpty => Items.Count == 0;

    public bool HasActive => Items.Any(i => i.Item.IsActive);

    public bool HasFinished => Items.Any(i => i.Item.IsFinished);

    /// <summary>Overall progress of running downloads, 0–1 (for the ring on the toolbar button).</summary>
    public double ActiveFraction
    {
        get
        {
            var active = Items.Where(i => i.Item.IsActive && i.Item.TotalBytes is > 0).Select(i => i.Item).ToList();
            var total = active.Sum(i => i.TotalBytes!.Value);
            return total > 0 ? Math.Clamp((double)active.Sum(i => i.ReceivedBytes) / total, 0, 1) : 0;
        }
    }

    /// <summary>A running download has an unknown size: the ring spins instead of filling.</summary>
    public bool IsActiveIndeterminate => Items.Any(i => i.Item.IsActive && i.Item.TotalBytes is not > 0);

    public string ButtonToolTip
    {
        get
        {
            var active = Items.Count(i => i.Item.IsActive);
            return active switch
            {
                0 => "Downloads (Ctrl+J)",
                1 => "Downloads: 1 in progress",
                _ => $"Downloads: {active} in progress",
            };
        }
    }

    /// <summary>The folder new downloads go to.</summary>
    public string DownloadFolder => _settings.Current.DownloadFolder ?? KnownFolders.Downloads;

    /// <summary>A page started a download: the window shows the flyout.</summary>
    public event EventHandler? DownloadStarted;

    /// <summary>Retry of a browser download: open its address in a new tab (the runtime starts it again).</summary>
    public event EventHandler<Uri>? OpenUrlRequested;

    /// <summary>The Downloads page was requested ("Show all").</summary>
    public event EventHandler? ShowAllRequested;

    /// <summary>Loads the list after <see cref="DownloadManager.InitializeAsync"/>.</summary>
    public void Load()
    {
        foreach (var item in _manager.Items)
        {
            if (!_byId.ContainsKey(item.Id))
            {
                var vm = new DownloadItemViewModel(item, this);
                _byId[item.Id] = vm;
                Items.Add(vm);
            }
        }

        RefreshSummary(listChanged: true);
    }

    /// <summary>The flyout or page is shown again: re-check which finished files still exist.</summary>
    public void RefreshFiles()
    {
        foreach (var item in Items)
        {
            item.RefreshFileState();
        }
    }

    /// <summary>Pause/resume/cancel need a live operation for browser downloads (gone after a restart).</summary>
    internal bool CanControl(DownloadItem item) => item.Kind == DownloadKind.App || _interceptor.IsLive(item.Id);

    // ---------------------------------------------------------------- actions

    internal void Open(DownloadItemViewModel item)
    {
        item.RefreshFileState();
        if (!item.CanOpen)
        {
            return;
        }

        try
        {
            _shell.OpenFile(item.Item.FilePath);
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not open a downloaded file: {ex.GetType().Name}");
            MessageBox.Show($"Windows couldn't open \"{item.FileName}\".\n\n{ex.Message}", "AJBrowser", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    internal void ShowInFolder(DownloadItemViewModel item)
    {
        try
        {
            _shell.ShowInFolder(item.Item.FilePath);
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not show a download in its folder: {ex.GetType().Name}");
            MessageBox.Show($"The folder of \"{item.FileName}\" no longer exists.", "AJBrowser", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    [RelayCommand]
    private void OpenDownloadFolder()
    {
        try
        {
            Directory.CreateDirectory(DownloadFolder);
            _shell.OpenFolder(DownloadFolder);
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not open the download folder: {ex.GetType().Name}");
        }
    }

    [RelayCommand]
    private void ShowAll() => ShowAllRequested?.Invoke(this, EventArgs.Empty);

    internal void Pause(DownloadItemViewModel item)
    {
        if (item.Item.Kind == DownloadKind.App)
        {
            _manager.Pause(item.Id);
        }
        else
        {
            _interceptor.Pause(item.Id);
        }
    }

    internal void Resume(DownloadItemViewModel item)
    {
        if (item.Item.Kind == DownloadKind.App)
        {
            _manager.Resume(item.Id);
        }
        else
        {
            _interceptor.Resume(item.Id);
        }
    }

    internal void Cancel(DownloadItemViewModel item)
    {
        if (item.Item.Kind == DownloadKind.App)
        {
            _manager.Cancel(item.Id);
        }
        else
        {
            _interceptor.Cancel(item.Id);
        }
    }

    /// <summary>
    /// App downloads start again in the manager. Browser downloads can't be restarted without a page, so the original
    /// address opens in a new tab and the runtime downloads it again (it becomes a new entry; the old one is removed).
    /// One-time links may have expired by then.
    /// </summary>
    internal void Retry(DownloadItemViewModel item)
    {
        if (item.Item.Kind == DownloadKind.App)
        {
            _manager.Retry(item.Id);
            return;
        }

        if (Uri.TryCreate(item.Item.SourceUrl, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            _ = RemoveAsync(item);
            OpenUrlRequested?.Invoke(this, uri);
        }
    }

    /// <summary>
    /// Browser downloads: the full address (the user's own link, like Chrome). App downloads (detected media from
    /// Phase 5) often carry signed tokens, so they copy the shortened address (plan Phase 4).
    /// </summary>
    internal void CopyLink(DownloadItemViewModel item)
    {
        var text = item.Item.Kind == DownloadKind.Browser ? item.Item.SourceUrl : item.DisplayUrl;
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            _log.Debug($"Clipboard busy: {ex.GetType().Name}"); // another app holds the clipboard
        }
    }

    internal async Task RemoveAsync(DownloadItemViewModel item)
    {
        try
        {
            await _manager.RemoveAsync(item.Id);
        }
        catch (Exception ex)
        {
            _log.Error("Removing a download from the list failed", ex);
        }
    }

    /// <summary>Removes finished entries from the list. Files on disk are not touched.</summary>
    [RelayCommand]
    private async Task ClearFinished()
    {
        try
        {
            var count = await _manager.ClearFinishedAsync();
            _log.Info($"Cleared {count} finished download(s) from the list");
        }
        catch (Exception ex)
        {
            _log.Error("Clearing the download list failed", ex);
        }
    }

    // ---------------------------------------------------------------- manager events (UI thread)

    private void OnItemChanged(object? sender, DownloadItem item)
    {
        if (_byId.TryGetValue(item.Id, out var existing))
        {
            existing.Update(item);
            RefreshSummary(listChanged: false);
            return;
        }

        var vm = new DownloadItemViewModel(item, this);
        _byId[item.Id] = vm;
        Items.Insert(0, vm);
        RefreshSummary(listChanged: true);
    }

    private void OnItemRemoved(object? sender, long id)
    {
        if (_byId.Remove(id, out var vm))
        {
            Items.Remove(vm);
            RefreshSummary(listChanged: true);
        }
    }

    private void OnDownloadStarted(object? sender, long id) => DownloadStarted?.Invoke(this, EventArgs.Empty);

    private void RefreshSummary(bool listChanged)
    {
        if (listChanged)
        {
            var newest = Items.Take(FlyoutCount).ToList();
            if (!newest.SequenceEqual(Recent))
            {
                Recent.Clear();
                foreach (var item in newest)
                {
                    Recent.Add(item);
                }
            }
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasFinished));
        OnPropertyChanged(nameof(HasActive));
        OnPropertyChanged(nameof(ActiveFraction));
        OnPropertyChanged(nameof(IsActiveIndeterminate));
        OnPropertyChanged(nameof(ButtonToolTip));
    }

    public void Dispose()
    {
        _manager.ItemChanged -= OnItemChanged;
        _manager.ItemRemoved -= OnItemRemoved;
        _interceptor.DownloadStarted -= OnDownloadStarted;
    }
}
