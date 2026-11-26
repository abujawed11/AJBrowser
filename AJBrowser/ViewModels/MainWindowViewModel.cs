using AJBrowser.Core.Infrastructure;
using AJBrowser.Core.Logging;
using AJBrowser.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AJBrowser.ViewModels;

/// <summary>
/// State and commands for the main window. Phase 1 has exactly one tab (<see cref="ActiveTab"/>);
/// Phase 2 turns this into a collection of tabs.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly ThemeService _theme;
    private readonly ShellService _shell;
    private readonly AppLogger _log;

    public MainWindowViewModel(AppPaths paths, ThemeService theme, ShellService shell, TabViewModel activeTab, AppLogger log)
    {
        _theme = theme;
        _shell = shell;
        _log = log;
        DataFolder = paths.Root;
        ActiveTab = activeTab;
    }

    public TabViewModel ActiveTab { get; }

    public string DataFolder { get; }

    public bool IsSystemTheme => _theme.Theme == AppTheme.System;
    public bool IsLightTheme => _theme.Theme == AppTheme.Light;
    public bool IsDarkTheme => _theme.Theme == AppTheme.Dark;

    public bool IsDebugBuild =>
#if DEBUG
        true;
#else
        false;
#endif

    [RelayCommand]
    private void SetTheme(AppTheme theme)
    {
        _theme.Apply(theme);
        _log.Info($"Theme changed to {theme}");
        OnPropertyChanged(nameof(IsSystemTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsDarkTheme));
    }

    [RelayCommand]
    private void OpenDataFolder() => _shell.OpenFolder(DataFolder);

    /// <summary>DEBUG helper: proves the global exception handler logs (redacted) and keeps the app alive.</summary>
    [RelayCommand]
    private void ThrowTestException() =>
        throw new InvalidOperationException(
            "Test exception from the debug button. This URL token must be redacted in the log: https://example.com/video.mp4?token=SHOULD_NOT_APPEAR");

    public void Dispose() => ActiveTab.Dispose();
}
