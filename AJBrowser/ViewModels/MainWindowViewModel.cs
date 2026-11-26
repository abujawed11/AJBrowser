using AJBrowser.Core.Infrastructure;
using AJBrowser.Core.Logging;
using AJBrowser.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AJBrowser.ViewModels;

/// <summary>
/// State and commands for the main window. In Phase 1 this grows tabs and navigation;
/// for now it only proves the MVVM wiring, theming and diagnostics work.
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly ThemeService _theme;
    private readonly ShellService _shell;
    private readonly AppLogger _log;

    public MainWindowViewModel(AppPaths paths, ThemeService theme, ShellService shell, AppLogger log)
    {
        _theme = theme;
        _shell = shell;
        _log = log;
        DataFolder = paths.Root;
        SelectedTheme = theme.Theme;
    }

    public IReadOnlyList<AppTheme> Themes { get; } = Enum.GetValues<AppTheme>();

    /// <summary>Changing this (via the ComboBox) switches the theme immediately.</summary>
    [ObservableProperty]
    public partial AppTheme SelectedTheme { get; set; }

    public string DataFolder { get; }

    public bool IsDebugBuild =>
#if DEBUG
        true;
#else
        false;
#endif

    partial void OnSelectedThemeChanged(AppTheme value)
    {
        if (_theme.Theme != value || value == AppTheme.System)
        {
            _theme.Apply(value);
            _log.Info($"Theme changed to {value}");
        }
    }

    [RelayCommand]
    private void OpenDataFolder() => _shell.OpenFolder(DataFolder);

    /// <summary>DEBUG helper: proves the global exception handler logs (redacted) and keeps the app alive.</summary>
    [RelayCommand]
    private void ThrowTestException() =>
        throw new InvalidOperationException(
            "Test exception from the debug button. This URL token must be redacted in the log: https://example.com/video.mp4?token=SHOULD_NOT_APPEAR");
}
