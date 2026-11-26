namespace AJBrowser.Core.Navigation;

/// <summary>AJBrowser's own pages, shown in a tab as WPF views (not web content).</summary>
public enum InternalPage
{
    History,
    Bookmarks,
    Settings,
}

/// <summary>
/// Addresses for internal pages, like Chrome's chrome://history: <c>ajbrowser://history</c>, <c>ajbrowser://bookmarks</c>,
/// <c>ajbrowser://settings</c>. They can be typed in the address bar and are saved in the session like web pages.
/// They are never handed to WebView2.
/// </summary>
public static class InternalPages
{
    public const string Scheme = "ajbrowser";

    public static string ToUrl(InternalPage page) => $"{Scheme}://{page.ToString().ToLowerInvariant()}";

    public static string TitleOf(InternalPage page) => page switch
    {
        InternalPage.History => "History",
        InternalPage.Bookmarks => "Bookmarks",
        InternalPage.Settings => "Settings",
        _ => page.ToString(),
    };

    /// <summary>Recognises "ajbrowser://history" (any case, optional trailing slash). Anything else returns null.</summary>
    public static InternalPage? TryParse(string? text)
    {
        var trimmed = text?.Trim();
        var prefix = Scheme + "://";
        if (trimmed is null || !trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = trimmed[prefix.Length..].TrimEnd('/');
        foreach (var page in Enum.GetValues<InternalPage>())
        {
            if (string.Equals(name, page.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return page;
            }
        }

        return null;
    }
}
