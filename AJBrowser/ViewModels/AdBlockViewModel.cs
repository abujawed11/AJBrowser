using AJBrowser.Browser;
using AJBrowser.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AJBrowser.ViewModels;

/// <summary>
/// Ad blocker state for the UI: the toolbar shield, its panel and the Settings page all bind to this one object,
/// so they always agree. Since Phase 8c it covers both engines (uBlock Origin Lite or AJBrowser's own).
/// </summary>
public sealed partial class AdBlockViewModel : ObservableObject, IDisposable
{
    private readonly AdBlockService _adBlocker;
    private readonly Func<TabViewModel?> _selectedTab;
    private readonly Action<Uri> _openInNewTab;
    private bool _busy;

    public AdBlockViewModel(AdBlockService adBlocker, Func<TabViewModel?> selectedTab, Action<Uri> openInNewTab)
    {
        _adBlocker = adBlocker;
        _selectedTab = selectedTab;
        _openInNewTab = openInNewTab;
        _adBlocker.StateChanged += OnStateChanged;
    }

    /// <summary>The chosen ad blocker can be switched (installed / can run on this PC).</summary>
    public bool IsAvailable => _adBlocker.IsAvailable;

    /// <summary>
    /// Ads are being blocked. Bound two-way to the switches: setting it switches the ad blocker. The getter always
    /// reports the real state, so a failed switch snaps back.
    /// </summary>
    public bool IsEnabled
    {
        get => _adBlocker.IsAvailable && _adBlocker.IsBlocking;
        set
        {
            if (value != IsEnabled && ToggleCommand.CanExecute(null))
            {
                ToggleCommand.Execute(null); // raises PropertyChanged when done
            }
            else
            {
                OnPropertyChanged(); // nothing to do (or busy): put the switch back where it belongs
            }
        }
    }

    public string Headline =>
        !_adBlocker.IsAvailable ? "Ad blocker unavailable"
        : !_adBlocker.IsBlocking ? "Ads are not blocked"
        : _adBlocker.UsesOwnEngine && _adBlocker.IsDownloadingLists ? "Getting filter lists ready…"
        : "Ads and trackers are blocked";

    public string Detail =>
        !_adBlocker.IsAvailable
            ? (_adBlocker.UsesOwnEngine
                ? $"AJBrowser's engine can't run: {AdBlockService.OwnEngineUnavailableReason}"
                : "It's still starting, or it failed to load (details in the log: ⋯ → Open data folder).")
        : _adBlocker.IsBlocking ? "On every site. Turn it off if a site doesn't work properly."
        : "Turn it on to block ads and trackers on every site.";

    public string ToolTip =>
        !_adBlocker.IsAvailable ? "Ad blocker unavailable"
        : _adBlocker.IsBlocking ? "Ad blocker: on"
        : "Ad blocker: off";

    /// <summary>For example "uBlock Origin Lite 2026.1124.1811" or "AJBrowser's engine".</summary>
    public string Engine =>
        _adBlocker.UsesOwnEngine ? "AJBrowser's engine (uBlock Origin's filter lists)"
        : _adBlocker.Extensions.Version is { } version ? $"{ExtensionManager.ExtensionName} {version}"
        : ExtensionManager.ExtensionName;

    // ---- engine choice (Settings)

    /// <summary>Settings radio button: uBlock Origin Lite.</summary>
    public bool IsUBlockOriginLiteEngine
    {
        get => _adBlocker.Engine == AdBlockEngineKind.UBlockOriginLite;
        set
        {
            if (value)
            {
                ChooseEngine(AdBlockEngineKind.UBlockOriginLite);
            }
        }
    }

    /// <summary>Settings radio button: AJBrowser's own engine.</summary>
    public bool IsOwnEngine
    {
        get => _adBlocker.Engine == AdBlockEngineKind.AJBrowser;
        set
        {
            if (value)
            {
                ChooseEngine(AdBlockEngineKind.AJBrowser);
            }
        }
    }

    /// <summary>AJBrowser's engine can run here (its DLL is present); the radio button is disabled otherwise.</summary>
    public bool IsOwnEngineSupported => AdBlockService.OwnEngineUnavailableReason is null;

    /// <summary>A line under the engine choice: rules and last update, downloading, or a problem.</summary>
    public string EngineStatus =>
        _adBlocker.OwnEngineStatus
        ?? (_adBlocker.Extensions.IsAvailable
            ? $"{ExtensionManager.ExtensionName} {_adBlocker.Extensions.Version}: its rules update with AJBrowser releases."
            : $"{ExtensionManager.ExtensionName} installs when the first tab opens.");

    /// <summary>The switch. The current page reloads so the change shows at once.</summary>
    [RelayCommand(CanExecute = nameof(IsAvailable))]
    private async Task Toggle()
    {
        await _adBlocker.SetEnabledAsync(!_adBlocker.Enabled);
        await ReloadCurrentPageAsync();
    }

    /// <summary>uBlock Origin Lite's own dashboard, in a new tab (only while it's the chosen engine).</summary>
    [RelayCommand(CanExecute = nameof(CanOpenSettings))]
    private void OpenSettings()
    {
        if (_adBlocker.Extensions.SettingsPageUrl is { } url)
        {
            _openInNewTab(url);
        }
    }

    private bool CanOpenSettings() => !_adBlocker.UsesOwnEngine && _adBlocker.Extensions.IsAvailable;

    private async void ChooseEngine(AdBlockEngineKind engine)
    {
        // async void: bound to a radio button; everything awaited is non-throwing.
        if (_busy || engine == _adBlocker.Engine)
        {
            OnStateChanged(this, EventArgs.Empty);
            return;
        }

        _busy = true;
        try
        {
            await _adBlocker.SetEngineAsync(engine);
            await ReloadCurrentPageAsync();
        }
        finally
        {
            _busy = false;
            OnStateChanged(this, EventArgs.Empty);
        }
    }

    private async Task ReloadCurrentPageAsync()
    {
        if (_selectedTab() is { IsInternal: false } tab)
        {
            // Tabs have just added or removed their page scripts. WebView2 applies such changes only to navigations
            // that start after it processed them; a reload in the same dispatcher turn still got the old scripts
            // (found in the real app), so let pending work run first.
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            await tab.ReloadCommand.ExecuteAsync(null);
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(Headline));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(ToolTip));
        OnPropertyChanged(nameof(Engine));
        OnPropertyChanged(nameof(IsUBlockOriginLiteEngine));
        OnPropertyChanged(nameof(IsOwnEngine));
        OnPropertyChanged(nameof(EngineStatus));
        ToggleCommand.NotifyCanExecuteChanged();
        OpenSettingsCommand.NotifyCanExecuteChanged();
    }

    public void Dispose() => _adBlocker.StateChanged -= OnStateChanged;
}
