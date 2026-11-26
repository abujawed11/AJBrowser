using AJBrowser.Core.Navigation;

namespace AJBrowser.Core.Tests.Navigation;

public class ZoomLevelsTests
{
    [Theory]
    [InlineData(1.0, 1.1)]
    [InlineData(1.1, 1.25)]
    [InlineData(1.17, 1.25)] // between steps (Ctrl+mouse wheel) -> next step up
    [InlineData(5.0, 5.0)]   // stays at the maximum
    [InlineData(0.1, 0.25)]
    public void Zoom_in(double current, double expected) => Assert.Equal(expected, ZoomLevels.ZoomIn(current));

    [Theory]
    [InlineData(1.0, 0.9)]
    [InlineData(0.9, 0.8)]
    [InlineData(1.17, 1.1)]
    [InlineData(0.25, 0.25)] // stays at the minimum
    [InlineData(9.0, 5.0)]
    public void Zoom_out(double current, double expected) => Assert.Equal(expected, ZoomLevels.ZoomOut(current));

    [Theory]
    [InlineData(1.0, "100%")]
    [InlineData(1.25, "125%")]
    [InlineData(0.67, "67%")]
    [InlineData(1.1749, "117%")]
    public void Percent_text(double zoom, string expected) => Assert.Equal(expected, ZoomLevels.ToPercentText(zoom));

    [Fact]
    public void Default_is_100_percent()
    {
        Assert.True(ZoomLevels.IsDefault(1.0));
        Assert.True(ZoomLevels.IsDefault(1.0000001));
        Assert.False(ZoomLevels.IsDefault(1.1));
    }
}

public class FindStatusTests
{
    [Theory]
    [InlineData(null, 0, 0, "")]
    [InlineData("", 1, 3, "")]
    [InlineData("cat", 0, 0, "No results")]
    [InlineData("cat", -1, 0, "No results")]
    [InlineData("cat", 1, 3, "1/3")]
    [InlineData("cat", 3, 3, "3/3")]
    [InlineData("cat", -1, 3, "3 found")] // active match not known yet
    [InlineData("cat", 4, 3, "3 found")]
    public void Formats_the_counter(string? term, int active, int count, string expected) =>
        Assert.Equal(expected, FindStatus.Format(term, active, count));
}

public class CommandLineUrlsTests
{
    private readonly UrlOrSearchResolver _resolver = new("https://search.test/?q={query}");

    [Fact]
    public void Parses_urls_files_and_searches_and_skips_options()
    {
        var file = @"C:\temp\page.html";
        var urls = CommandLineUrls.Parse(
            ["--diagnostics", "example.com", file, "hello world", "javascript:alert(1)", "", "-x"],
            _resolver,
            path => path == file);

        Assert.Equal(
            ["https://example.com/", "file:///C:/temp/page.html", "https://search.test/?q=hello%20world"],
            urls.Select(u => u.AbsoluteUri));
    }

    [Fact]
    public void Missing_files_are_not_treated_as_files()
    {
        var urls = CommandLineUrls.Parse([@"C:\nope\missing.html"], _resolver, _ => false);

        Assert.Single(urls);
        Assert.Equal("search.test", urls[0].Host);
    }

    [Fact]
    public void No_arguments_means_no_urls()
    {
        Assert.Empty(CommandLineUrls.Parse([], _resolver, _ => false));
    }
}
