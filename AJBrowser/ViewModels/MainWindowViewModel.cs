using System.Collections.ObjectModel;
using AJBrowser.Browser;
using AJBrowser.Core.Infrastructure;
using AJBrowser.Core.Logging;
using AJBrowser.Core.Tabs;
using AJBrowser.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AJBrowser.ViewModels;

/// <summary>
/// State and commands for the main window: the tab list, the selected tab, closed-tab history, fullscreen,
/// theme and debug tools. Each tab's own state lives in <see cref="TabViewModel"/>.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly ThemeService _theme;
    private readonly ShellService _shell;
    private readonly ExtensionManager _adBlocker;
    private readonly Func<TabViewModel> _createTab;
    private readonly AppLogger _log;
    private readonly ClosedTabStack _closedTabs = new();

    public MainWindowViewModel(AppPaths paths, ThemeService theme, ShellService shell, ExtensionManager adBlocker, Func<TabViewModel> createTab, AppLogger log)
    {
        _theme = theme;
        _shell = shell;
        _adBlocker = adBlocker;
        _adBlocker.StateChanged += OnAdBlockerStateChanged;
        _createTab = createTab;
        _log = log;
        DataFolder = paths.Root;
    }

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFullScreen), nameof(IsChromeVisible))]
    public partial TabViewModel? SelectedTab { get; set; }

    /// <summary>F11 fullscreen (the user's choice), as opposed to a page's video going fullscreen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFullScreen), nameof(IsChromeVisible))]
    public partial bool IsAppFullScreen { get; set; }

    /// <summary>The window should cover the whole screen (F11, or the selected page's HTML5 fullscreen).</summary>
    public bool IsFullScreen => IsAppFullScreen || SelectedTab?.IsPageFullScreen == true;

    /// <summary>Tab strip and toolbar are hidden in fullscreen.</summary>
    public bool IsChromeVisible => !IsFullScreen;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReopenClosedTabCommand))]
    public partial bool CanReopenClosedTab { get; set; }

    public string DataFolder { get; }

    public bool IsSystemTheme => _theme.Theme == AppTheme.System;
    public bool IsLightTheme => _theme.Theme == AppTheme.Light;
    public bool IsDarkTheme => _theme.Theme == AppTheme.Dark;

    /// <summary>The ad blocker is installed and can be switched (its menu items are greyed out otherwise).</summary>
    public bool IsAdBlockAvailable => _adBlocker.IsAvailable;

    /// <summary>
    /// Ads are being blocked. Bound two-way to the switch in the shield panel: setting it switches the ad blocker.
    /// The getter always reports the real state, so a failed switch snaps back.
    /// </summary>
    public bool IsAdBlockEnabled
    {
        get => _adBlocker.IsAvailable && _adBlocker.IsEnabled;
        set
        {
            if (value != IsAdBlockEnabled && ToggleAdBlockCommand.CanExecute(null))
            {
                ToggleAdBlockCommand.Execute(null); // raises PropertyChanged when done
            }
            else
            {
                OnPropertyChanged(); // nothing to do (or busy): put the switch back where it belongs
            }
        }
    }

    /// <summary>Headline in the shield panel.</summary>
    public string AdBlockHeadline =>
        !_adBlocker.IsAvailable ? "Ad blocker unavailable"
        : _adBlocker.IsEnabled ? "Ads and trackers are blocked"
        : "Ads are not blocked";

    /// <summary>Second line in the shield panel.</summary>
    public string AdBlockDetail =>
        !_adBlocker.IsAvailable ? "It's still starting, or it failed to load (details in the log: ⋯ → Open data folder)."
        : _adBlocker.IsEnabled ? "On every site. Turn it off if a site doesn't work properly."
        : "Turn it on to block ads and trackers on every site.";

    /// <summary>Shield button tooltip.</summary>
    public string AdBlockToolTip =>
        !_adBlocker.IsAvailable ? "Ad blocker unavailable"
        : _adBlocker.IsEnabled ? "Ad blocker: on"
        : "Ad blocker: off";

    /// <summary>Small print in the shield panel, for example "uBlock Origin Lite 2026.1124.1811".</summary>
    public string AdBlockEngine => _adBlocker.IsAvailable ? $"uBlock Origin Lite {_adBlocker.Version}" : "uBlock Origin Lite";

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

    // ---------------------------------------------------------------- opening tabs

    /// <summary>Opens the startup tabs: the given URLs, or the home page.</summary>
    public void OpenStartupTabs(IReadOnlyList<Uri> urls)
    {
        _log.Info($"Opening {Math.Max(1, urls.Count)} startup tab(s)");
        if (urls.Count == 0)
        {
            OpenTab(null, select: true);
            return;
        }

        for (var i = 0; i < urls.Count; i++)
        {
            OpenTab(urls[i], select: i == 0);
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
        tab.NewTabRequested += OnTabNewTabRequested;
        tab.PopupRequested += OnTabPopupRequested;
        tab.CloseRequested += OnTabCloseRequested;
        tab.PropertyChanged += OnTabPropertyChanged;

        var position = index ?? (opener is not null ? Tabs.IndexOf(opener) + 1 : Tabs.Count);
        Tabs.Insert(Math.Clamp(position, 0, Tabs.Count), tab);

        if (select || SelectedTab is null)
        {
            SelectedTab = tab;
        }

        return tab;
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

    private void OnTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender == SelectedTab && e.PropertyName == nameof(TabViewModel.IsPageFullScreen))
        {
            OnPropertyChanged(nameof(IsFullScreen));
            OnPropertyChanged(nameof(IsChromeVisible));
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
    }

    // ---------------------------------------------------------------- closing tabs

    /// <summary>Ctrl+W, the tab's ✕, middle-click, or window.close().</summary>
    [RelayCommand]
    private void CloseTab(TabViewModel? tab)
    {
        tab ??= SelectedTab;
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

        if (tab.Url is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
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
        tab.PropertyChanged -= OnTabPropertyChanged;
        tab.Dispose();

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

    [RelayCommand]
    private void SetTheme(AppTheme theme)
    {
        _theme.Apply(theme);
        _log.Info($"Theme changed to {theme}");
        OnPropertyChanged(nameof(IsSystemTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsDarkTheme));
    }

    /// <summary>The switch in the shield panel. The current page reloads so the change shows at once.</summary>
    [RelayCommand(CanExecute = nameof(IsAdBlockAvailable))]
    private async Task ToggleAdBlock()
    {
        await _adBlocker.SetEnabledAsync(!_adBlocker.IsEnabled);
        if (SelectedTab is { } tab)
        {
            await tab.ReloadCommand.ExecuteAsync(null);
        }
    }

    /// <summary>Shield panel → Ad blocker settings: uBlock Origin Lite's own dashboard, in a new tab.</summary>
    [RelayCommand(CanExecute = nameof(IsAdBlockAvailable))]
    private void OpenAdBlockSettings()
    {
        if (_adBlocker.SettingsPageUrl is { } url)
        {
            OpenTab(url, select: true, opener: SelectedTab);
        }
    }

    private void OnAdBlockerStateChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IsAdBlockAvailable));
        OnPropertyChanged(nameof(IsAdBlockEnabled));
        OnPropertyChanged(nameof(AdBlockHeadline));
        OnPropertyChanged(nameof(AdBlockDetail));
        OnPropertyChanged(nameof(AdBlockToolTip));
        OnPropertyChanged(nameof(AdBlockEngine));
        ToggleAdBlockCommand.NotifyCanExecuteChanged();
        OpenAdBlockSettingsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void OpenDataFolder() => _shell.OpenFolder(DataFolder);

    /// <summary>DEBUG helper: proves the global exception handler logs (redacted) and keeps the app alive.</summary>
    [RelayCommand]
    private void ThrowTestException() =>
        throw new InvalidOperationException(
            "Test exception from the debug button. This URL token must be redacted in the log: https://example.com/video.mp4?token=SHOULD_NOT_APPEAR");

    public void Dispose()
    {
        _adBlocker.StateChanged -= OnAdBlockerStateChanged;
        foreach (var tab in Tabs)
        {
            tab.Dispose();
        }

        Tabs.Clear();
    }
}
