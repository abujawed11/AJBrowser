using AJBrowser.Core.Navigation;

namespace AJBrowser.Core.Settings;

/// <summary>A search engine preset. <see cref="Template"/> contains <see cref="UrlOrSearchResolver.QueryPlaceholder"/>.</summary>
public sealed record SearchEngine(string Id, string Name, string Template);

/// <summary>The built-in search engines, plus a user-defined "custom" template.</summary>
public static class SearchEngines
{
    public const string CustomId = "custom";

    public static SearchEngine Bing { get; } = new("bing", "Bing", "https://www.bing.com/search?q={query}");
    public static SearchEngine Google { get; } = new("google", "Google", "https://www.google.com/search?q={query}");
    public static SearchEngine DuckDuckGo { get; } = new("duckduckgo", "DuckDuckGo", "https://duckduckgo.com/?q={query}");
    public static SearchEngine Brave { get; } = new("brave", "Brave Search", "https://search.brave.com/search?q={query}");

    public static IReadOnlyList<SearchEngine> Presets { get; } = [Bing, Google, DuckDuckGo, Brave];

    public static SearchEngine Default => Bing;

    public static SearchEngine? FindPreset(string? id) =>
        Presets.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The template the address bar should use for these settings (falls back to the default engine).</summary>
    public static string TemplateFor(AppSettings settings)
    {
        if (string.Equals(settings.SearchEngine, CustomId, StringComparison.OrdinalIgnoreCase)
            && UrlOrSearchResolver.IsValidSearchTemplate(settings.CustomSearchTemplate))
        {
            return settings.CustomSearchTemplate.Trim();
        }

        return (FindPreset(settings.SearchEngine) ?? Default).Template;
    }
}
