using System.IO;
using System.Reflection;
using AJBrowser.Core.Navigation;
using AJBrowser.Core.Settings;
using AJBrowser.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AJBrowser.ViewModels.Pages;

/// <summary>A choice in the search engine box.</summary>
public sealed record SearchEngineOption(string Id, string Name);

/// <summary>
/// ajbrowser://settings. Every control writes straight to <see cref="SettingsStore"/> (no Save button), and the page
/// refreshes when settings change elsewhere (for example the theme from the ⋯ menu). Text fields (home page,
/// custom search address) are checked and applied when you press Enter or leave the box.
/// </summary>
public sealed partial class SettingsPageViewModel : ObservableObject, IInternalPage
{
    private readonly SettingsStore _settings;
    private readonly ShellService _shell;
    private readonly IPageHost _host;
    private readonly string _dataFolder;
    private bool _refreshing;

    public SettingsPageViewModel(SettingsStore settings, AdBlockViewModel adBlock, ShellService shell, IPageHost host, string dataFolder)
    {
        _settings = settings;
        _shell = shell;
        _host = host;
        _dataFolder = dataFolder;
        AdBlock = adBlock;
        SearchEngineOptions = [.. SearchEngines.Presets.Select(e => new SearchEngineOption(e.Id, e.Name)), new SearchEngineOption(SearchEngines.CustomId, "Custom…")];
        _settings.Changed += OnSettingsChanged;
        Refresh();
    }

    public InternalPage Kind => InternalPage.Settings;

    public string Title => InternalPages.TitleOf(InternalPage.Settings);

    /// <summary>The same ad-block switch as the toolbar shield.</summary>
    public AdBlockViewModel AdBlock { get; }

    // ---- appearance

    public bool IsThemeSystem { get => _settings.Current.Theme == ThemeSetting.System; set => SetTheme(value, ThemeSetting.System); }
    public bool IsThemeLight { get => _settings.Current.Theme == ThemeSetting.Light; set => SetTheme(value, ThemeSetting.Light); }
    public bool IsThemeDark { get => _settings.Current.Theme == ThemeSetting.Dark; set => SetTheme(value, ThemeSetting.Dark); }

    public bool ShowBookmarksBar
    {
        get => _settings.Current.ShowBookmarksBar;
        set => _settings.Update(s => s with { ShowBookmarksBar = value });
    }

    // ---- on startup

    public bool IsStartupHomePage
    {
        get => _settings.Current.Startup == StartupBehavior.HomePage;
        set { if (value) _settings.Update(s => s with { Startup = StartupBehavior.HomePage }); }
    }

    public bool IsStartupRestore
    {
        get => _settings.Current.Startup == StartupBehavior.RestoreSession;
        set { if (value) _settings.Update(s => s with { Startup = StartupBehavior.RestoreSession }); }
    }

    [ObservableProperty]
    public partial string HomePageText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string HomePageError { get; set; } = string.Empty;

    // ---- search

    public IReadOnlyList<SearchEngineOption> SearchEngineOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomSearch))]
    public partial SearchEngineOption? SelectedSearchEngine { get; set; }

    public bool IsCustomSearch => SelectedSearchEngine?.Id == SearchEngines.CustomId;

    [ObservableProperty]
    public partial string CustomSearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CustomSearchError { get; set; } = string.Empty;

    // ---- downloads (used from Phase 4)

    public string DownloadFolderText => _settings.Current.DownloadFolder ?? $"{DefaultDownloadFolder} (default)";

    public bool HasCustomDownloadFolder => _settings.Current.DownloadFolder is not null;

    public bool AskWhereToSave
    {
        get => _settings.Current.AskWhereToSave;
        set => _settings.Update(s => s with { AskWhereToSave = value });
    }

    /// <summary>The Windows Downloads folder (the usual location; a moved Downloads folder is picked up in Phase 4).</summary>
    public static string DefaultDownloadFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    // ---- about

    public string VersionText =>
        $"AJBrowser {Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "unknown"}";

    public void Activate() => Refresh();

    /// <summary>Enter or leaving the home page box.</summary>
    [RelayCommand]
    private void ApplyHomePage()
    {
        if (AppSettings.TryParseHomePage(HomePageText) is not { } uri)
        {
            HomePageError = "Enter a web address, for example example.com or https://example.com/";
            return;
        }

        HomePageError = string.Empty;
        _settings.Update(s => s with { HomePage = uri.AbsoluteUri });
        HomePageText = _settings.Current.HomePage;
    }

    partial void OnSelectedSearchEngineChanged(SearchEngineOption? value)
    {
        if (_refreshing || value is null)
        {
            return;
        }

        if (value.Id != SearchEngines.CustomId)
        {
            CustomSearchError = string.Empty;
            _settings.Update(s => s with { SearchEngine = value.Id });
        }
        else if (UrlOrSearchResolver.IsValidSearchTemplate(CustomSearchText))
        {
            _settings.Update(s => s with { SearchEngine = SearchEngines.CustomId, CustomSearchTemplate = CustomSearchText.Trim() });
        }

        // Custom without a valid address yet: wait for ApplyCustomSearch.
    }

    /// <summary>Enter or leaving the custom search box.</summary>
    [RelayCommand]
    private void ApplyCustomSearch()
    {
        if (!UrlOrSearchResolver.IsValidSearchTemplate(CustomSearchText))
        {
            CustomSearchError = "Enter an address with {query} where the search words go, for example https://example.com/search?q={query}";
            return;
        }

        CustomSearchError = string.Empty;
        _settings.Update(s => s with { SearchEngine = SearchEngines.CustomId, CustomSearchTemplate = CustomSearchText.Trim() });
    }

    /// <summary>The folder picked in "Change…" (the dialog lives in the view).</summary>
    public void SetDownloadFolder(string folder) => _settings.Update(s => s with { DownloadFolder = folder });

    [RelayCommand]
    private void ResetDownloadFolder() => _settings.Update(s => s with { DownloadFolder = null });

    [RelayCommand]
    private void OpenHistory() => _host.Open(new Uri(InternalPages.ToUrl(InternalPage.History)), OpenTarget.CurrentTab);

    [RelayCommand]
    private void OpenDataFolder() => _shell.OpenFolder(_dataFolder);

    [RelayCommand]
    private void OpenNotices()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md");
        if (File.Exists(file))
        {
            _shell.OpenTextFile(file);
        }
    }

    private void SetTheme(bool selected, ThemeSetting theme)
    {
        if (selected)
        {
            _settings.Update(s => s with { Theme = theme });
        }
    }

    private void Refresh()
    {
        _refreshing = true;
        try
        {
            var s = _settings.Current;
            HomePageText = s.HomePage;
            HomePageError = string.Empty;
            SelectedSearchEngine = SearchEngineOptions.FirstOrDefault(o => string.Equals(o.Id, s.SearchEngine, StringComparison.OrdinalIgnoreCase))
                                   ?? SearchEngineOptions[0];
            if (s.CustomSearchTemplate.Length > 0 || CustomSearchText.Length == 0)
            {
                CustomSearchText = s.CustomSearchTemplate;
            }

            foreach (var name in new[]
                     {
                         nameof(IsThemeSystem), nameof(IsThemeLight), nameof(IsThemeDark), nameof(ShowBookmarksBar),
                         nameof(IsStartupHomePage), nameof(IsStartupRestore), nameof(DownloadFolderText),
                         nameof(HasCustomDownloadFolder), nameof(AskWhereToSave),
                     })
            {
                OnPropertyChanged(name);
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings previous) => Refresh();

    public void Dispose() => _settings.Changed -= OnSettingsChanged;
}
