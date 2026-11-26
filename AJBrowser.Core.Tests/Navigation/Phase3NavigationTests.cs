using System.Globalization;
using AJBrowser.Core.Navigation;

namespace AJBrowser.Core.Tests.Navigation;

public sealed class InternalPagesTests
{
    [Theory]
    [InlineData("ajbrowser://history", InternalPage.History)]
    [InlineData("AJBrowser://Bookmarks/", InternalPage.Bookmarks)]
    [InlineData("  ajbrowser://settings  ", InternalPage.Settings)]
    public void Parses_internal_addresses(string text, InternalPage expected) => Assert.Equal(expected, InternalPages.TryParse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("history")]
    [InlineData("ajbrowser://nope")]
    [InlineData("https://history")]
    [InlineData("ajbrowser:history")]
    public void Ignores_everything_else(string? text) => Assert.Null(InternalPages.TryParse(text));

    [Fact]
    public void Round_trips() =>
        Assert.All(Enum.GetValues<InternalPage>(), p => Assert.Equal(p, InternalPages.TryParse(InternalPages.ToUrl(p))));
}

public sealed class HistoryRulesTests
{
    [Theory]
    [InlineData("https://example.com/a#section", "https://example.com/a")]
    [InlineData("http://example.com/", "http://example.com/")]
    [InlineData("https://example.com/?q=1", "https://example.com/?q=1")]
    public void Records_web_pages_without_fragment(string url, string expected) => Assert.Equal(expected, HistoryRules.UrlToRecord(url));

    [Theory]
    [InlineData(null)]
    [InlineData("about:blank")]
    [InlineData("data:text/html,hi")]
    [InlineData("file:///C:/a.html")]
    [InlineData("chrome-extension://abc/dashboard.html")]
    [InlineData("ajbrowser://history")]
    [InlineData("not a url")]
    public void Skips_everything_else(string? url) => Assert.Null(HistoryRules.UrlToRecord(url));

    [Fact]
    public void Day_labels()
    {
        var today = new DateTime(2026, 11, 26, 15, 0, 0);
        var en = CultureInfo.GetCultureInfo("en-GB");

        Assert.Equal("Today", HistoryRules.DayLabel(today.AddHours(-10), today, en));
        Assert.Equal("Yesterday", HistoryRules.DayLabel(today.AddDays(-1), today, en));
        Assert.Equal(new DateTime(2026, 11, 24).ToString("D", en), HistoryRules.DayLabel(today.AddDays(-2), today, en));
        Assert.Contains("24 November 2026", HistoryRules.DayLabel(today.AddDays(-2), today, en));
    }

    [Fact]
    public void Clear_ranges()
    {
        var now = new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(now.AddHours(-1), HistoryRules.RangeStartUtc(ClearRange.LastHour, now));
        Assert.Equal(now.AddDays(-7), HistoryRules.RangeStartUtc(ClearRange.Last7Days, now));
        Assert.Null(HistoryRules.RangeStartUtc(ClearRange.AllTime, now));
    }
}

public sealed class SuggestionRankerTests
{
    private static readonly DateTime Now = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

    private static SuggestionCandidate H(string url, string title = "", int visits = 1, double daysAgo = 30) =>
        new(url, title, visits, Now.AddDays(-daysAgo), IsBookmark: false);

    [Fact]
    public void Address_prefix_beats_title_and_contains_matches()
    {
        var ranked = SuggestionRanker.Rank("git", [
            H("https://example.com/git-guide", "Guide"),        // contains in address
            H("https://news.example/", "Git news"),             // title word
            H("https://github.com/", "GitHub"),                 // address prefix (after https://)
        ], Now);

        Assert.Equal(["https://github.com/", "https://news.example/", "https://example.com/git-guide"], ranked.Select(s => s.Url));
    }

    [Fact]
    public void Www_is_ignored_for_prefix_matching()
    {
        var ranked = SuggestionRanker.Rank("exa", [H("https://www.example.com/")], Now);

        Assert.Equal(100 + 2, ranked.Single().Score); // prefix match + 1 visit
    }

    [Fact]
    public void Host_part_prefix_matches()
    {
        Assert.True(SuggestionRanker.Score("mail", H("https://mail.google.com/"), Now) >= 100);
        Assert.True(SuggestionRanker.Score("google", H("https://mail.google.com/"), Now) is >= 80 and < 100);
    }

    [Fact]
    public void Visits_bookmarks_and_recency_break_ties()
    {
        var ranked = SuggestionRanker.Rank("site", [
            H("https://site-a.example/"),
            H("https://site-b.example/", visits: 10),
            new SuggestionCandidate("https://site-c.example/", "", 0, null, IsBookmark: true),
            H("https://site-d.example/", daysAgo: 0.1),
        ], Now);

        Assert.Equal("https://site-c.example/", ranked[0].Url); // +25 bookmark
        Assert.Equal("https://site-b.example/", ranked[1].Url); // +20 visits
        Assert.Equal("https://site-d.example/", ranked[2].Url); // +10 recent
    }

    [Fact]
    public void Duplicates_from_history_and_bookmarks_merge()
    {
        var ranked = SuggestionRanker.Rank("exa", [
            H("https://example.com/", "Example", visits: 3),
            new SuggestionCandidate("https://example.com/", "My bookmark", 0, null, IsBookmark: true),
        ], Now);

        var only = Assert.Single(ranked);
        Assert.True(only.IsBookmark);
        Assert.Equal("Example", only.Title);
    }

    [Fact]
    public void No_match_empty_query_and_limit()
    {
        Assert.Empty(SuggestionRanker.Rank("zzz", [H("https://example.com/")], Now));
        Assert.Empty(SuggestionRanker.Rank("  ", [H("https://example.com/")], Now));

        var many = Enumerable.Range(0, 20).Select(i => H($"https://ex{i}.example/"));
        Assert.Equal(SuggestionRanker.DefaultLimit, SuggestionRanker.Rank("ex", many, Now).Count);
    }
}

public sealed class SearchTemplateTests
{
    [Fact]
    public void Template_can_be_changed_at_runtime()
    {
        var resolver = new UrlOrSearchResolver("https://www.bing.com/search?q={query}");
        resolver.SearchTemplate = "https://duckduckgo.com/?q={query}";

        Assert.Equal("https://duckduckgo.com/?q=two%20words", resolver.Resolve("two words").Uri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://duckduckgo.com/?q={query}", true)]
    [InlineData("http://intranet/search?q={query}", true)]
    [InlineData("https://example.com/search", false)]
    [InlineData("ftp://example.com/{query}", false)]
    [InlineData("javascript:{query}", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Validates_templates(string? template, bool valid) => Assert.Equal(valid, UrlOrSearchResolver.IsValidSearchTemplate(template));

    [Fact]
    public void Invalid_template_is_refused()
    {
        var resolver = new UrlOrSearchResolver("https://www.bing.com/search?q={query}");

        Assert.Throws<ArgumentException>(() => resolver.SearchTemplate = "https://example.com/");
        Assert.Equal("https://www.bing.com/search?q={query}", resolver.SearchTemplate);
    }
}
