namespace AJBrowser.Core.Navigation;

/// <summary>
/// Hard-coded defaults until settings arrive in Phase 3 (homepage and search engine become user choices then).
/// </summary>
public static class BrowserDefaults
{
    /// <summary>Search URL; <see cref="UrlOrSearchResolver.QueryPlaceholder"/> is replaced by the escaped search text.</summary>
    public const string SearchUrlTemplate = "https://www.bing.com/search?q={query}";

    public static Uri HomePage { get; } = new("https://www.bing.com/");
}
