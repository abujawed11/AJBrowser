using AJBrowser.Browser;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AJBrowser.ViewModels;

/// <summary>
/// Ad blocker state for the UI: the toolbar shield, its panel and the Settings page all bind to this one object,
/// so they always agree.
/// </summary>
public sealed partial class AdBlockViewModel : ObservableObject, IDisposable
{
    private readonly ExtensionManager _adBlocker;
    private readonly Func<TabViewModel?> _selectedTab;
    private readonly Action<Uri> _openInNewTab;

    public AdBlockViewModel(ExtensionManager adBlocker, Func<TabViewModel?> selectedTab, Action<Uri> openInNewTab)
    {
        _adBlocker = adBlocker;
        _selectedTab = selectedTab;
        _openInNewTab = openInNewTab;
        _adBlocker.StateChanged += OnStateChanged;
    }

    /// <summary>The ad blocker is installed and can be switched.</summary>
    public bool IsAvailable => _adBlocker.IsAvailable;

    /// <summary>
    /// Ads are being blocked. Bound two-way to the switches: setting it switches the ad blocker. The getter always
    /// reports the real state, so a failed switch snaps back.
    /// </summary>
    public bool IsEnabled
    {
        get => _adBlocker.IsAvailable && _adBlocker.IsEnabled;
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
        : _adBlocker.IsEnabled ? "Ads and trackers are blocked"
        : "Ads are not blocked";

    public string Detail =>
        !_adBlocker.IsAvailable ? "It's still starting, or it failed to load (details in the log: ⋯ → Open data folder)."
        : _adBlocker.IsEnabled ? "On every site. Turn it off if a site doesn't work properly."
        : "Turn it on to block ads and trackers on every site.";

    public string ToolTip =>
        !_adBlocker.IsAvailable ? "Ad blocker unavailable"
        : _adBlocker.IsEnabled ? "Ad blocker: on"
        : "Ad blocker: off";

    /// <summary>For example "uBlock Origin Lite 2026.1124.1811".</summary>
    public string Engine => _adBlocker.IsAvailable ? $"uBlock Origin Lite {_adBlocker.Version}" : "uBlock Origin Lite";

    /// <summary>The switch. The current page reloads so the change shows at once.</summary>
    [RelayCommand(CanExecute = nameof(IsAvailable))]
    private async Task Toggle()
    {
        await _adBlocker.SetEnabledAsync(!_adBlocker.IsEnabled);
        if (_selectedTab() is { IsInternal: false } tab)
        {
            await tab.ReloadCommand.ExecuteAsync(null);
        }
    }

    /// <summary>uBlock Origin Lite's own dashboard, in a new tab.</summary>
    [RelayCommand(CanExecute = nameof(IsAvailable))]
    private void OpenSettings()
    {
        if (_adBlocker.SettingsPageUrl is { } url)
        {
            _openInNewTab(url);
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
        ToggleCommand.NotifyCanExecuteChanged();
        OpenSettingsCommand.NotifyCanExecuteChanged();
    }

    public void Dispose() => _adBlocker.StateChanged -= OnStateChanged;
}
