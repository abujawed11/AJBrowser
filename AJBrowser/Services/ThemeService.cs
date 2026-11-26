using System.Windows;
using Microsoft.Win32;

namespace AJBrowser.Services;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Applies light/dark themes in two layers:
/// 1. WPF's built-in Fluent theme (Application.ThemeMode) styles standard controls.
/// 2. Our own Themes/Light.xaml or Themes/Dark.xaml supplies semantic brushes for custom chrome.
/// When the theme is System, it follows the Windows "app mode" setting live.
/// </summary>
public sealed class ThemeService : IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private static readonly Uri LightUri = new("/AJBrowser;component/Themes/Light.xaml", UriKind.Relative);
    private static readonly Uri DarkUri = new("/AJBrowser;component/Themes/Dark.xaml", UriKind.Relative);

    private readonly Application _app;

    public ThemeService(Application app)
    {
        _app = app;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public AppTheme Theme { get; private set; } = AppTheme.System;

    /// <summary>The theme actually shown (System resolved to Light or Dark).</summary>
    public bool IsDark { get; private set; }

    public event EventHandler? ThemeChanged;

    public void Apply(AppTheme theme)
    {
        Theme = theme;
        var dark = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => IsWindowsInDarkMode(),
        };

        // ThemeMode is still marked experimental in .NET 10 (diagnostic WPF0001). Suppressed only here
        // so the experimental surface stays in one place. See IMPLEMENTATION_PLAN.md, item V9.
#pragma warning disable WPF0001
        _app.ThemeMode = theme switch
        {
            AppTheme.Light => ThemeMode.Light,
            AppTheme.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
#pragma warning restore WPF0001

        SwapSemanticDictionary(dark ? DarkUri : LightUri);

        IsDark = dark;
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

    private void SwapSemanticDictionary(Uri source)
    {
        var dictionaries = _app.Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(d => d.Source == LightUri || d.Source == DarkUri);

        if (existing?.Source == source)
        {
            return;
        }

        var replacement = new ResourceDictionary { Source = source };
        if (existing is null)
        {
            dictionaries.Add(replacement);
        }
        else
        {
            dictionaries[dictionaries.IndexOf(existing)] = replacement;
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (Theme != AppTheme.System || e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color))
        {
            return;
        }

        // SystemEvents can fire on a background thread; resources must change on the UI thread.
        _app.Dispatcher.BeginInvoke(() =>
        {
            if (Theme == AppTheme.System && IsWindowsInDarkMode() != IsDark)
            {
                Apply(AppTheme.System);
            }
        });
    }

    private static bool IsWindowsInDarkMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }
}
