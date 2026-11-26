using System.Globalization;

namespace AJBrowser.Core.Navigation;

/// <summary>Which pages go into history, and how history is grouped for display.</summary>
public static class HistoryRules
{
    /// <summary>
    /// The URL to record for a visited page, or null if it shouldn't be recorded. Only http(s) pages are kept
    /// (not about:, data:, file:, extension or internal pages). The #fragment is dropped, so jumping around one
    /// page doesn't create many entries.
    /// </summary>
    public static string? UrlToRecord(string? url)
    {
        if (string.IsNullOrEmpty(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || uri.Host.Length == 0)
        {
            return null;
        }

        var hash = url.IndexOf('#');
        return hash < 0 ? url : url[..hash];
    }

    /// <summary>"Today", "Yesterday", or the full date ("Monday, 24 November 2026" in English) for older days.</summary>
    public static string DayLabel(DateTime localDay, DateTime localToday, CultureInfo culture)
    {
        var day = localDay.Date;
        var today = localToday.Date;
        if (day == today)
        {
            return "Today";
        }

        if (day == today.AddDays(-1))
        {
            return "Yesterday";
        }

        return day.ToString("D", culture);
    }

    /// <summary>Start (inclusive, UTC) of a "Clear browsing data" time range; null = all time.</summary>
    public static DateTime? RangeStartUtc(ClearRange range, DateTime nowUtc) => range switch
    {
        ClearRange.LastHour => nowUtc.AddHours(-1),
        ClearRange.Last24Hours => nowUtc.AddDays(-1),
        ClearRange.Last7Days => nowUtc.AddDays(-7),
        ClearRange.Last4Weeks => nowUtc.AddDays(-28),
        _ => null,
    };
}

/// <summary>Time ranges offered by "Clear browsing data".</summary>
public enum ClearRange
{
    LastHour,
    Last24Hours,
    Last7Days,
    Last4Weeks,
    AllTime,
}
