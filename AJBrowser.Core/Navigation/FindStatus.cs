namespace AJBrowser.Core.Navigation;

/// <summary>Text for the find bar's match counter.</summary>
public static class FindStatus
{
    /// <summary>
    /// "3/12", "No results", or "" while nothing is searched. <paramref name="activeMatch"/> is treated as a
    /// 1-based position (WebView2 passes Chromium's ordinal; -1 means not known yet). See plan §10 V8.
    /// </summary>
    public static string Format(string? term, int activeMatch, int matchCount)
    {
        if (string.IsNullOrEmpty(term))
        {
            return string.Empty;
        }

        if (matchCount <= 0)
        {
            return "No results";
        }

        return activeMatch >= 1 && activeMatch <= matchCount
            ? $"{activeMatch}/{matchCount}"
            : $"{matchCount} found";
    }
}
