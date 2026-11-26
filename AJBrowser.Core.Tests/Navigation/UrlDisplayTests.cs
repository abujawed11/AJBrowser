using AJBrowser.Core.Navigation;

namespace AJBrowser.Core.Tests.Navigation;

public class UrlDisplayTests
{
    [Theory]
    [InlineData("https://example.com/", "example.com")]
    [InlineData("https://example.com/path?q=1", "example.com/path?q=1")]
    [InlineData("https://example.com/#top", "example.com/#top")]
    [InlineData("https://example.com/?q=1", "example.com/?q=1")]
    [InlineData("http://example.com/", "http://example.com")]
    [InlineData("http://localhost:3000/api", "http://localhost:3000/api")]
    [InlineData("about:blank", "about:blank")]
    [InlineData("file:///C:/temp/page.html", "file:///C:/temp/page.html")]
    [InlineData("not a url", "not a url")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Formats_urls_for_the_address_bar(string? url, string expected)
    {
        Assert.Equal(expected, UrlDisplay.ForAddressBar(url));
    }
}
