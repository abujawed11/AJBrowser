namespace AJBrowser.Core.Navigation;

/// <summary>A page that might be suggested: from history and/or bookmarks.</summary>
public sealed record SuggestionCandidate(string Url, string Title, int VisitCount, DateTime? LastVisitUtc, bool IsBookmark);

/// <summary>A ranked suggestion for the address-bar dropdown.</summary>
public sealed record Suggestion(string Url, string Title, bool IsBookmark, int Score);

/// <summary>
/// Orders address-bar suggestions. Simple and predictable rather than clever:
/// <list type="number">
/// <item>How the text matches: start of the address (after https:// and www.) &gt; start of a host part
/// &gt; start of a title word &gt; anywhere in the address &gt; anywhere in the title. No match = not suggested.</item>
/// <item>Then bonuses: bookmarked, visited often, visited recently.</item>
/// </list>
/// </summary>
public static class SuggestionRanker
{
    public const int DefaultLimit = 6;

    public static IReadOnlyList<Suggestion> Rank(string text, IEnumerable<SuggestionCandidate> candidates, DateTime nowUtc, int limit = DefaultLimit)
    {
        var query = text.Trim();
        if (query.Length == 0)
        {
            return [];
        }

        // Merge duplicates (the same page from history and bookmarks).
        var merged = new Dictionary<string, SuggestionCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (merged.TryGetValue(candidate.Url, out var existing))
            {
                merged[candidate.Url] = existing with
                {
                    Title = existing.Title.Length > 0 ? existing.Title : candidate.Title,
                    VisitCount = Math.Max(existing.VisitCount, candidate.VisitCount),
                    LastVisitUtc = Max(existing.LastVisitUtc, candidate.LastVisitUtc),
                    IsBookmark = existing.IsBookmark || candidate.IsBookmark,
                };
            }
            else
            {
                merged[candidate.Url] = candidate;
            }
        }

        return merged.Values
            .Select(c => (Candidate: c, Score: Score(query, c, nowUtc)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Candidate.Url.Length) // shorter (more general) address first on ties
            .Take(limit)
            .Select(x => new Suggestion(x.Candidate.Url, x.Candidate.Title, x.Candidate.IsBookmark, x.Score))
            .ToList();
    }

    /// <summary>0 = no match. Public for tests.</summary>
    public static int Score(string query, SuggestionCandidate candidate, DateTime nowUtc)
    {
        var match = MatchScore(query, candidate);
        if (match == 0)
        {
            return 0;
        }

        var score = match;
        score += Math.Min(candidate.VisitCount, 20) * 2;
        if (candidate.IsBookmark)
        {
            score += 25;
        }

        if (candidate.LastVisitUtc is { } last)
        {
            var age = nowUtc - last;
            score += age < TimeSpan.FromDays(1) ? 10 : age < TimeSpan.FromDays(7) ? 5 : 0;
        }

        return score;
    }

    private static int MatchScore(string query, SuggestionCandidate candidate)
    {
        var address = StripForMatching(candidate.Url);
        if (address.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        var host = address.Split('/', 2)[0];
        if (host.Split('.').Any(part => part.StartsWith(query, StringComparison.OrdinalIgnoreCase)))
        {
            return 80;
        }

        if (candidate.Title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(word => word.StartsWith(query, StringComparison.OrdinalIgnoreCase)))
        {
            return 60;
        }

        if (address.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 30;
        }

        return candidate.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ? 20 : 0;
    }

    /// <summary>"https://www.example.com/a" → "example.com/a", so typing "exa" matches the start.</summary>
    public static string StripForMatching(string url)
    {
        var text = url;
        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            text = text[(schemeEnd + 3)..];
        }

        if (text.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            text = text[4..];
        }

        return text;
    }

    private static DateTime? Max(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : (a > b ? a : b);
}
