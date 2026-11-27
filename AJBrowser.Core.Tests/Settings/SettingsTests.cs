using AJBrowser.Core.Navigation;
using AJBrowser.Core.Settings;

namespace AJBrowser.Core.Tests.Settings;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AJBrowserTests", Guid.NewGuid().ToString("N"));

    private string File => Path.Combine(_root, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var store = SettingsStore.Load(File);

        Assert.Equal(new AppSettings(), store.Current);
        Assert.Null(store.LoadProblem);
    }

    [Fact]
    public void Update_saves_and_reload_round_trips()
    {
        var store = SettingsStore.Load(File);
        store.Update(s => s with
        {
            HomePage = "https://example.com/",
            Startup = StartupBehavior.RestoreSession,
            SearchEngine = "duckduckgo",
            Theme = ThemeSetting.Dark,
            ShowBookmarksBar = false,
            DownloadFolder = @"D:\Downloads",
            AskWhereToSave = true,
        });

        var reloaded = SettingsStore.Load(File).Current;

        Assert.Equal(store.Current, reloaded);
        Assert.Equal(ThemeSetting.Dark, reloaded.Theme);
        Assert.False(System.IO.File.Exists(File + ".tmp"));
    }

    [Fact]
    public void Enums_are_saved_as_readable_text()
    {
        var store = SettingsStore.Load(File);
        store.Update(s => s with { Startup = StartupBehavior.RestoreSession });

        Assert.Contains("\"RestoreSession\"", System.IO.File.ReadAllText(File));
    }

    [Fact]
    public void Ad_block_engine_and_switch_round_trip_as_text()
    {
        var store = SettingsStore.Load(File);
        Assert.Equal(AdBlockEngineKind.UBlockOriginLite, store.Current.AdBlockEngine);
        Assert.Null(store.Current.AdBlockEnabled); // not chosen yet: the app carries uBO Lite's old choice over

        store.Update(s => s with { AdBlockEngine = AdBlockEngineKind.AJBrowser, AdBlockEnabled = false });

        Assert.Contains("\"AJBrowser\"", System.IO.File.ReadAllText(File));
        var reloaded = SettingsStore.Load(File).Current;
        Assert.Equal(AdBlockEngineKind.AJBrowser, reloaded.AdBlockEngine);
        Assert.False(reloaded.AdBlockEnabled);
    }

    [Fact]
    public void Unknown_ad_block_engine_number_falls_back_to_ublock_origin_lite()
    {
        Assert.Equal(AdBlockEngineKind.UBlockOriginLite, (new AppSettings() with { AdBlockEngine = (AdBlockEngineKind)42 }).Normalize().AdBlockEngine);
    }

    [Fact]
    public void Missing_fields_get_defaults()
    {
        Directory.CreateDirectory(_root);
        System.IO.File.WriteAllText(File, """{ "Theme": "Light" }""");

        var settings = SettingsStore.Load(File).Current;

        Assert.Equal(ThemeSetting.Light, settings.Theme);
        Assert.Equal(AppSettings.DefaultHomePage, settings.HomePage);
        Assert.True(settings.ShowBookmarksBar);
    }

    [Fact]
    public void Invalid_values_are_replaced_by_defaults()
    {
        Directory.CreateDirectory(_root);
        System.IO.File.WriteAllText(File, """{ "HomePage": "javascript:alert(1)", "SearchEngine": "nope", "DownloadFolder": "  " }""");

        var settings = SettingsStore.Load(File).Current;

        Assert.Equal(AppSettings.DefaultHomePage, settings.HomePage);
        Assert.Equal(SearchEngines.Default.Id, settings.SearchEngine);
        Assert.Null(settings.DownloadFolder);
    }

    [Fact]
    public void Unreadable_file_gives_defaults_and_keeps_a_copy()
    {
        Directory.CreateDirectory(_root);
        System.IO.File.WriteAllText(File, "{ not json");

        var store = SettingsStore.Load(File);

        Assert.Equal(new AppSettings(), store.Current);
        Assert.NotNull(store.LoadProblem);
        Assert.True(System.IO.File.Exists(File + ".bad"));
    }

    [Fact]
    public void Changed_is_raised_with_the_previous_settings_and_not_for_no_change()
    {
        var store = SettingsStore.Load(File);
        var raised = new List<AppSettings>();
        store.Changed += (_, previous) => raised.Add(previous);

        store.Update(s => s with { Theme = ThemeSetting.Dark });
        store.Update(s => s with { Theme = ThemeSetting.Dark }); // same again

        Assert.Equal(ThemeSetting.System, Assert.Single(raised).Theme);
    }
}

public sealed class AppSettingsTests
{
    [Theory]
    [InlineData("https://example.com/", "https://example.com/")]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("  http://intranet.local/start  ", "http://intranet.local/start")]
    [InlineData("about:blank", "about:blank")]
    [InlineData("file:///C:/start.html", "file:///C:/start.html")]
    public void Accepts_valid_home_pages(string text, string expected) =>
        Assert.Equal(expected, AppSettings.TryParseHomePage(text)!.AbsoluteUri);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hi")]
    [InlineData("about:settings")]
    [InlineData("two words")]
    [InlineData("ftp://example.com/")]
    public void Rejects_invalid_home_pages(string? text) => Assert.Null(AppSettings.TryParseHomePage(text));

    [Fact]
    public void Custom_engine_with_a_bad_template_falls_back_to_the_default()
    {
        var settings = new AppSettings { SearchEngine = SearchEngines.CustomId, CustomSearchTemplate = "https://example.com/no-placeholder" }.Normalize();

        Assert.Equal(SearchEngines.Default.Id, settings.SearchEngine);
    }

    [Fact]
    public void Template_for_presets_and_custom()
    {
        Assert.Equal(SearchEngines.Google.Template, SearchEngines.TemplateFor(new AppSettings { SearchEngine = "GOOGLE" }));
        Assert.Equal("https://s.example/?q={query}",
            SearchEngines.TemplateFor(new AppSettings { SearchEngine = "custom", CustomSearchTemplate = " https://s.example/?q={query} " }));
        Assert.Equal(SearchEngines.Default.Template, SearchEngines.TemplateFor(new AppSettings { SearchEngine = "unknown" }));
    }

    [Fact]
    public void All_presets_are_valid_templates() =>
        Assert.All(SearchEngines.Presets, e => Assert.True(UrlOrSearchResolver.IsValidSearchTemplate(e.Template), e.Id));
}
