using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using AJBrowser.Browser;
using AJBrowser.Core.Infrastructure;
using AJBrowser.Core.Logging;
using AJBrowser.Core.Navigation;
using AJBrowser.Core.Persistence;
using AJBrowser.Core.Settings;
using AJBrowser.Core.Tabs;
using AJBrowser.Services;
using AJBrowser.ViewModels.Pages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AJBrowser.ViewModels;

/// <summary>A bookmark or folder on the bookmarks bar (folders carry their contents for the drop-down menu).</summary>
public sealed class BookmarkNodeViewModel(Bookmark bookmark, IReadOnlyList<BookmarkNodeViewModel> children, System.Windows.Media.ImageSource? icon = null)
{
    public Bookmark Bookmark { get; } = bookmark;

    /// <summary>The site's icon (null for folders and unknown sites: then the glyph is shown).</summary>
    public System.Windows.Media.ImageSource? Icon { get; } = icon;

    public string Title => Bookmark.Title;

    public bool IsFolder => Bookmark.IsFolder;

    public IReadOnlyList<BookmarkNodeViewModel> Children { get; } = children;

    public string ToolTip => IsFolder ? Title : $"{Title}\n{Bookmark.Url}";

    public string Glyph => IsFolder ? "" : "";
}

/// <summary>
/// State and commands for the main window: the tab list, the selected tab, closed-tab history, fullscreen,
/// theme, bookmarks bar, internal pages, session saving and debug tools. Each tab's own state lives in <see cref="TabViewModel"/>.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly SettingsStore _settings;
    private readonly ShellService _shell;
    private readonly BookmarkService _bookmarks;
    private readonly FaviconService _favicons;
    private readonly SessionService _session;
    private readonly Func<TabViewModel> _createTab;
    private readonly AppLogger _log;
    private readonly ClosedTabStack _closedTabs = new();

    public MainWindowViewModel(
        AppPaths paths,
        SettingsStore settings,
        ShellService shell,
        ExtensionManager adBlocker,
        BookmarkService bookmarks,
        FaviconService favicons,
        SessionService session,
        DownloadsViewModel downloads,
        Func<TabViewModel> createTab,
        AppLogger log)
    {
        _settings = settings;
        _shell = shell;
        _bookmarks = bookmarks;
        _favicons = favicons;
        _session = session;
        _createTab = createTab;
        _log = log;
        DataFolder = paths.Root;
        Downloads = downloads;
        Downloads.OpenUrlRequested += OnDownloadsOpenUrlRequested;
        Downloads.ShowAllRequested += OnDownloadsShowAllRequested;
        AdBlock = new AdBlockViewModel(adBlocker, () => SelectedTab, url => OpenTab(url, select: true, opener: SelectedTab));

        _settings.Changed += OnSettingsChanged;
        _bookmarks.Changed += OnBookmarksChanged;
        _favicons.Changed += OnBookmarksChanged; // a new site icon may belong on the bar
        Tabs.CollectionChanged += OnTabsCollectionChanged;
    }

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFullScreen), nameof(IsChromeVisible), nameof(IsBookmarksBarShown))]
    public partial TabViewModel? SelectedTab { get; set; }

    /// <summary>F11 fullscreen (the user's choice), as opposed to a page's video going fullscreen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFullScreen), nameof(IsChromeVisible), nameof(IsBookmarksBarShown))]
    public partial bool IsAppFullScreen { get; set; }

    /// <summary>The window should cover the whole screen (F11, or the selected page's HTML5 fullscreen).</summary>
    public bool IsFullScreen => IsAppFullScreen || SelectedTab?.IsPageFullScreen == true;

    /// <summary>Tab strip and toolbar are hidden in fullscreen.</summary>
    public bool IsChromeVisible => !IsFullScreen;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReopenClosedTabCommand))]
    public partial bool CanReopenClosedTab { get; set; }

    public string DataFolder { get; }

    /// <summary>The toolbar download button, its flyout and the Downloads page.</summary>
    public DownloadsViewModel Downloads { get; }

    /// <summary>The toolbar shield and its panel (and the switch on the Settings page).</summary>
    public AdBlockViewModel AdBlock { get; }

    public bool IsSystemTheme => _settings.Current.Theme == ThemeSetting.System;
    public bool IsLightTheme => _settings.Current.Theme == ThemeSetting.Light;
    public bool IsDarkTheme => _settings.Current.Theme == ThemeSetting.Dark;

    /// <summary>Top-level bookmarks and folders.</summary>
    public ObservableCollection<BookmarkNodeViewModel> BookmarksBarItems { get; } = [];

    public bool IsBookmarksBarShown => _settings.Current.ShowBookmarksBar && IsChromeVisible;

    public bool IsBookmarksBarEmpty => BookmarksBarItems.Count == 0;

    public bool IsDebugBuild =>
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>Ask the view to put the caret in the address bar (new tab, Ctrl+L).</summary>
    public event EventHandler? FocusAddressBarRequested;

    /// <summary>The last tab was closed: the window should close.</summary>
    public event EventHandler? CloseWindowRequested;

    // ---------------------------------------------------------------- startup

    /// <summary>
    /// Opens the startup tabs: the saved session (if "Continue where you left off" is on), then any URLs from the
    /// command line; or the home page if there's nothing else. Restored background tabs load when first selected.
    /// </summary>
    public async Task OpenStartupTabsAsync(IReadOnlyList<Uri> commandLineUrls)
    {
        if (_settings.Current.Startup == StartupBehavior.RestoreSession)
        {
            var saved = await _session.LoadAsync();
            var restorable = saved.Where(t => Uri.TryCreate(t.Url, UriKind.Absolute, out _)).ToList();
            if (restorable.Count > 0)
            {
                _log.Info($"Restoring {restorable.Count} tab(s) from the last session");
                foreach (var savedTab in restorable)
                {
                    var tab = _createTab();
                    tab.StartDeferred(new Uri(savedTab.Url), savedTab.Title);
                    InsertTab(tab, Tabs.Count, select: false);
                }

                // Selecting the active tab is what makes it load.
                var active = restorable.FindIndex(t => t.IsActive);
                SelectedTab = Tabs[active >= 0 ? active : 0];
            }
        }

        for (var i = 0; i < commandLineUrls.Count; i++)
        {
            OpenTab(commandLineUrls[i], select: i == 0);
        }

        if (Tabs.Count == 0)
        {
            _log.Info("Opening the home page");
            OpenTab(null, select: true);
        }

        ScheduleSessionSave();
    }

    // ---------------------------------------------------------------- opening tabs

    /// <summary>The window refused to close (downloads still running) after its last tab closed: show a home tab again.</summary>
    public void EnsureTab()
    {
        if (Tabs.Count == 0)
        {
            OpenTab(null, select: true);
        }
    }

    /// <summary>Ctrl+T or the "+" button: a new tab with the home page, address bar focused.</summary>
    [RelayCommand]
    private void NewTab()
    {
        OpenTab(null, select: true);
        FocusAddressBarRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adds a started tab at the end, or right after <paramref name="opener"/>.</summary>
    private TabViewModel OpenTab(Uri? uri, bool select, TabViewModel? opener = null, int? index = null)
    {
        var tab = AddTab(select, opener, index);
        _ = tab.StartAsync(uri); // never throws: problems show in the tab's page area
        return tab;
    }

    private TabViewModel AddTab(bool select, TabViewModel? opener, int? index = null)
    {
        var tab = _createTab();
        var position = index ?? (opener is not null ? Tabs.IndexOf(opener) + 1 : Tabs.Count);
        InsertTab(tab, position, select || SelectedTab is null);
        return tab;
    }

    private void InsertTab(TabViewModel tab, int position, bool select)
    {
        tab.NewTabRequested += OnTabNewTabRequested;
        tab.PopupRequested += OnTabPopupRequested;
        tab.CloseRequested += OnTabCloseRequested;
        tab.OnlyDownloaded += OnTabOnlyDownloaded;
        tab.PropertyChanged += OnTabPropertyChanged;

        Tabs.Insert(Math.Clamp(position, 0, Tabs.Count), tab);
        if (select)
        {
            SelectedTab = tab;
        }
    }

    private void OnTabNewTabRequested(object? sender, NewTabRequest request) =>
        OpenTab(request.Uri, request.Select, opener: sender as TabViewModel);

    private async void OnTabPopupRequested(object? sender, PopupRequest request)
    {
        // async void: an event handler. AdoptPopupAsync catches engine errors itself and always completes the request.
        try
        {
            var popupTab = AddTab(select: true, opener: sender as TabViewModel);
            await popupTab.AdoptPopupAsync(request);
        }
        catch (Exception ex)
        {
            _log.Error("Opening a popup as a tab failed", ex);
            request.Cancel();
        }
    }

    private void OnTabCloseRequested(object? sender, EventArgs e)
    {
        if (sender is TabViewModel tab)
        {
            CloseTab(tab);
        }
    }

    /// <summary>
    /// A tab that never showed a page only started a download (a download link opened in a new tab, or Retry): close
    /// it, like Chrome; its download keeps going (the WebView is kept, 4c). The last tab shows the home page instead,
    /// because closing it would close the window.
    /// </summary>
    private void OnTabOnlyDownloaded(object? sender, EventArgs e)
    {
        if (sender is not TabViewModel tab || !Tabs.Contains(tab))
        {
            return;
        }

        if (Tabs.Count > 1)
        {
            CloseTabCore(tab, rememberForReopen: false);
        }
        else
        {
            tab.HomeCommand.Execute(null);
        }
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender == SelectedTab && e.PropertyName == nameof(TabViewModel.IsPageFullScreen))
        {
            OnPropertyChanged(nameof(IsFullScreen));
            OnPropertyChanged(nameof(IsChromeVisible));
            OnPropertyChanged(nameof(IsBookmarksBarShown));
        }

        // Anything the saved session stores (address, title, page kind) changed.
        if (e.PropertyName is nameof(TabViewModel.Title) or nameof(TabViewModel.AddressText) or nameof(TabViewModel.InternalPageContent))
        {
            ScheduleSessionSave();
        }
    }

    partial void OnSelectedTabChanged(TabViewModel? oldValue, TabViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }

        ScheduleSessionSave();
    }

    // ---------------------------------------------------------------- internal pages and bookmarks

    [RelayCommand]
    private void ShowHistory() => OpenInternalPage(InternalPage.History);

    [RelayCommand]
    private void ShowBookmarks() => OpenInternalPage(InternalPage.Bookmarks);

    [RelayCommand]
    private void ShowSettings() => OpenInternalPage(InternalPage.Settings);

    /// <summary>Ctrl+J, or "Show all" in the downloads flyout.</summary>
    [RelayCommand]
    private void ShowDownloads() => OpenInternalPage(InternalPage.Downloads);

    private void OnDownloadsShowAllRequested(object? sender, EventArgs e) => OpenInternalPage(InternalPage.Downloads);

    /// <summary>For Retry/Resume of a video download: the newest address any open tab has seen for that file.</summary>
    public (Core.Media.DetectedMedia Media, Func<Uri, Task<string?>> Cookies)? FindMediaInTabs(string key)
    {
        // The selected tab first (most likely the one where the video was just replayed).
        foreach (var tab in Tabs.OrderByDescending(t => t == SelectedTab))
        {
            if (tab.FindMedia(key) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Retrying a browser download: its address opens in a new tab and the engine downloads it again.</summary>
    private void OnDownloadsOpenUrlRequested(object? sender, Uri uri) => OpenTab(uri, select: true, opener: SelectedTab);

    /// <summary>Ctrl+Shift+Delete: the History page with "Clear browsing data" open.</summary>
    [RelayCommand]
    private void ShowClearBrowsingData()
    {
        OpenInternalPage(InternalPage.History);
        (SelectedTab?.InternalPageContent as HistoryPageViewModel)?.OpenClearPanel();
    }

    /// <summary>Switches to a tab already showing the page, or opens it in a new tab (like Chrome's settings).</summary>
    public void OpenInternalPage(InternalPage page)
    {
        var existing = Tabs.FirstOrDefault(t => t.InternalPageContent?.Kind == page);
        if (existing is not null)
        {
            SelectedTab = existing;
            return;
        }

        OpenTab(new Uri(InternalPages.ToUrl(page)), select: true, opener: SelectedTab);
    }

    /// <summary>Ctrl+Shift+B.</summary>
    [RelayCommand]
    private void ToggleBookmarksBar() => _settings.Update(s => s with { ShowBookmarksBar = !s.ShowBookmarksBar });

    /// <summary>A bookmark on the bar (or in one of its folder menus) was clicked.</summary>
    public void OpenBookmark(BookmarkNodeViewModel node, OpenTarget target)
    {
        if (node.IsFolder || !Uri.TryCreate(node.Bookmark.Url, UriKind.Absolute, out var uri))
        {
            return;
        }

        if (SelectedTab is { } tab)
        {
            tab.Open(uri, target);
        }
        else
        {
            OpenTab(uri, select: true);
        }
    }

    private void OnBookmarksChanged(object? sender, EventArgs e)
    {
        BookmarkNodeViewModel Build(Bookmark b) =>
            new(b, b.IsFolder ? _bookmarks.ChildrenOf(b.Id).Select(Build).ToList() : [], _favicons.Get(b.Url));

        BookmarksBarItems.Clear();
        foreach (var bookmark in _bookmarks.ChildrenOf(null))
        {
            BookmarksBarItems.Add(Build(bookmark));
        }

        OnPropertyChanged(nameof(IsBookmarksBarEmpty));
    }

    // ---------------------------------------------------------------- closing tabs

    /// <summary>Ctrl+W, the tab's ✕, middle-click, or window.close().</summary>
    [RelayCommand]
    private void CloseTab(TabViewModel? tab) => CloseTabCore(tab ?? SelectedTab, rememberForReopen: true);

    private void CloseTabCore(TabViewModel? tab, bool rememberForReopen)
    {
        if (tab is null)
        {
            return;
        }

        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        _log.Debug($"Closing tab {index + 1} of {Tabs.Count}");

        // A blank tab (for example a popup that only started a download) isn't worth reopening.
        if (rememberForReopen && tab.Url is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && !url.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
        {
            _closedTabs.Push(new ClosedTab(uri, tab.Title, index));
            CanReopenClosedTab = true;
        }

        // Choose and show the next tab before removing this one, so a WebView is always on screen.
        var selectedIndex = SelectedTab is null ? -1 : Tabs.IndexOf(SelectedTab);
        var next = TabSelection.IndexAfterClose(index, selectedIndex, Tabs.Count);
        Tabs.RemoveAt(index);
        SelectedTab = next >= 0 ? Tabs[next] : null;

        tab.NewTabRequested -= OnTabNewTabRequested;
        tab.PopupRequested -= OnTabPopupRequested;
        tab.CloseRequested -= OnTabCloseRequested;
        tab.OnlyDownloaded -= OnTabOnlyDownloaded;
        tab.PropertyChanged -= OnTabPropertyChanged;
        tab.Dispose(); // a WebView that still owns downloads is kept until they finish (DownloadInterceptor.Park)

        if (Tabs.Count == 0)
        {
            CloseWindowRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Ctrl+Shift+T.</summary>
    [RelayCommand(CanExecute = nameof(CanReopenClosedTab))]
    private void ReopenClosedTab()
    {
        if (_closedTabs.TryPop(out var closed))
        {
            OpenTab(closed!.Url, select: true, index: closed.Index);
        }

        CanReopenClosedTab = _closedTabs.Count > 0;
    }

    // ---------------------------------------------------------------- switching tabs

    [RelayCommand]
    private void NextTab() => CycleTab(+1);

    [RelayCommand]
    private void PreviousTab() => CycleTab(-1);

    private void CycleTab(int step)
    {
        if (Tabs.Count > 1 && SelectedTab is not null)
        {
            SelectedTab = Tabs[TabSelection.Cycle(Tabs.IndexOf(SelectedTab), Tabs.Count, step)];
        }
    }

    /// <summary>Ctrl+1 … Ctrl+9.</summary>
    public void SelectTabByNumber(int number)
    {
        var index = TabSelection.ForNumberKey(number, Tabs.Count);
        if (index >= 0)
        {
            SelectedTab = Tabs[index];
        }
    }

    public void RequestFocusAddressBar() => FocusAddressBarRequested?.Invoke(this, EventArgs.Empty);

    // ---------------------------------------------------------------- session

    private void OnTabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleSessionSave();

    private void ScheduleSessionSave() => _session.ScheduleSave(SessionSnapshot);

    /// <summary>The open tabs as they should be restored next time.</summary>
    private IReadOnlyList<SessionTab> SessionSnapshot() =>
        Tabs.Where(t => !string.IsNullOrEmpty(t.Url))
            .Select(t => new SessionTab(t.Url!, t.Title, t == SelectedTab))
            .ToList();

    // ---------------------------------------------------------------- window-level commands

    [RelayCommand]
    private void ToggleFullScreen()
    {
        if (SelectedTab?.IsPageFullScreen == true && !IsAppFullScreen)
        {
            return; // a page video is fullscreen; Esc (handled by the page) leaves that
        }

        IsAppFullScreen = !IsAppFullScreen;
    }

    /// <summary>⋯ → Theme. The theme itself is applied by AppServices when the setting changes.</summary>
    [RelayCommand]
    private void SetTheme(ThemeSetting theme)
    {
        _settings.Update(s => s with { Theme = theme });
        _log.Info($"Theme changed to {theme}");
    }

    private void OnSettingsChanged(object? sender, AppSettings previous)
    {
        if (previous.DetectVideos && !_settings.Current.DetectVideos)
        {
            foreach (var tab in Tabs)
            {
                tab.ClearMedia();
            }
        }

        OnPropertyChanged(nameof(IsSystemTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(IsBookmarksBarShown));
    }

    [RelayCommand]
    private void OpenDataFolder() => _shell.OpenFolder(DataFolder);

    /// <summary>DEBUG helper: proves the global exception handler logs (redacted) and keeps the app alive.</summary>
    [RelayCommand]
    private void ThrowTestException() =>
        throw new InvalidOperationException(
            "Test exception from the debug button. This URL token must be redacted in the log: https://example.com/video.mp4?token=SHOULD_NOT_APPEAR");

    /// <summary>App exit: save the session one last time, then close every tab.</summary>
    public void Dispose()
    {
        _session.SaveNowAndStop(SessionSnapshot());

        _settings.Changed -= OnSettingsChanged;
        _bookmarks.Changed -= OnBookmarksChanged;
        _favicons.Changed -= OnBookmarksChanged;
        Tabs.CollectionChanged -= OnTabsCollectionChanged;
        Downloads.OpenUrlRequested -= OnDownloadsOpenUrlRequested;
        Downloads.ShowAllRequested -= OnDownloadsShowAllRequested;
        AdBlock.Dispose();
        foreach (var tab in Tabs)
        {
            tab.Dispose();
        }

        Tabs.Clear();
    }
}
