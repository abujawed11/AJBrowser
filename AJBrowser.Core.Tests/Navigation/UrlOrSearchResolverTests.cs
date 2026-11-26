using AJBrowser.Core.Navigation;

namespace AJBrowser.Core.Tests.Navigation;

public class UrlOrSearchResolverTests
{
    private const string Template = "https://search.test/?q={query}";
    private readonly UrlOrSearchResolver _resolver = new(Template);

    [Theory]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("EXAMPLE.com", "https://example.com/")]
    [InlineData("  example.com  ", "https://example.com/")]
    [InlineData("example.com.", "https://example.com./")]
    [InlineData("www.example.com/path?q=1#top", "https://www.example.com/path?q=1#top")]
    [InlineData("sub.example.co.uk", "https://sub.example.co.uk/")]
    [InlineData("example.com:8080", "https://example.com:8080/")]
    [InlineData("localhost", "http://localhost/")]
    [InlineData("localhost:3000", "http://localhost:3000/")]
    [InlineData("localhost:3000/api/items", "http://localhost:3000/api/items")]
    [InlineData("app.localhost", "http://app.localhost/")]
    [InlineData("127.0.0.1", "http://127.0.0.1/")]
    [InlineData("192.168.1.1:8080/admin", "http://192.168.1.1:8080/admin")]
    [InlineData("[::1]:5000", "http://[::1]:5000/")]
    [InlineData("intranet:8080", "http://intranet:8080/")]
    [InlineData("intranet/", "http://intranet/")]
    public void Host_like_input_becomes_a_url(string input, string expected)
    {
        var result = _resolver.Resolve(input);

        Assert.Equal(AddressKind.Url, result.Kind);
        Assert.Equal(expected, result.Uri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://example.com", "http://example.com/")]
    [InlineData("https://example.com/a b", "https://example.com/a%20b")]
    [InlineData("HTTPS://Example.com/Path", "https://example.com/Path")]
    [InlineData("ftp://example.com/file.txt", "ftp://example.com/file.txt")]
    [InlineData("file:///C:/temp/page.html", "file:///C:/temp/page.html")]
    [InlineData("about:blank", "about:blank")]
    [InlineData("data:text/html,hello", "data:text/html,hello")]
    [InlineData("mailto:someone@example.com", "mailto:someone@example.com")]
    public void Explicit_schemes_are_used_as_typed(string input, string expected)
    {
        var result = _resolver.Resolve(input);

        Assert.Equal(AddressKind.Url, result.Kind);
        Assert.Equal(expected, result.Uri!.AbsoluteUri);
    }

    [Fact]
    public void International_domain_names_are_urls()
    {
        var result = _resolver.Resolve("münchen.de");

        Assert.Equal(AddressKind.Url, result.Kind);
        Assert.Equal("xn--mnchen-3ya.de", result.Uri!.IdnHost);
        Assert.Equal("https", result.Uri.Scheme);
    }

    [Theory]
    [InlineData("best wpf tutorials", "best%20wpf%20tutorials")]
    [InlineData("what is example.com", "what%20is%20example.com")]
    [InlineData("hello", "hello")]
    [InlineData("c#", "c%23")]
    [InlineData("c++", "c%2B%2B")]
    [InlineData("3.14", "3.14")]
    [InlineData("1.5", "1.5")]
    [InlineData("999.1.1.1", "999.1.1.1")]
    [InlineData("example", "example")]
    [InlineData("readme", "readme")]
    [InlineData("-bad-.com", "-bad-.com")]
    [InlineData("example.123", "example.123")]
    [InlineData("user@example.com", "user%40example.com")]
    [InlineData("foo:bar", "foo%3Abar")]
    [InlineData("intranet/notes", "intranet%2Fnotes")]
    [InlineData("example.com:99999", "example.com%3A99999")]
    [InlineData("http://", "http%3A%2F%2F")]
    [InlineData("a&b=c", "a%26b%3Dc")]
    public void Other_input_becomes_a_search(string input, string expectedQuery)
    {
        var result = _resolver.Resolve(input);

        Assert.Equal(AddressKind.Search, result.Kind);
        Assert.Equal("https://search.test/?q=" + expectedQuery, result.Uri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("?example.com", "example.com")]
    [InlineData("?  localhost:3000 ", "localhost%3A3000")]
    public void Question_mark_prefix_forces_a_search(string input, string expectedQuery)
    {
        var result = _resolver.Resolve(input);

        Assert.Equal(AddressKind.Search, result.Kind);
        Assert.Equal("https://search.test/?q=" + expectedQuery, result.Uri!.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("?")]
    [InlineData("?   ")]
    public void Empty_input_does_nothing(string? input)
    {
        var result = _resolver.Resolve(input);

        Assert.Equal(AddressKind.Empty, result.Kind);
        Assert.Null(result.Uri);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData("  javascript:alert(document.cookie)")]
    [InlineData("javascript://%0aalert(1)")]
    [InlineData("javascript: alert(1)")]
    [InlineData("vbscript:msgbox(1)")]
    public void Script_urls_are_blocked(string input)
    {
        var result = _resolver.Resolve(input);

        Assert.Equal(AddressKind.Blocked, result.Kind);
        Assert.Null(result.Uri);
    }

    [Theory]
    [InlineData("https://search.test/")]
    [InlineData("not a url {query}")]
    [InlineData("javascript:{query}")]
    [InlineData("")]
    public void Invalid_search_templates_are_rejected(string template)
    {
        Assert.ThrowsAny<ArgumentException>(() => new UrlOrSearchResolver(template));
    }

    [Fact]
    public void Default_search_template_is_valid()
    {
        var resolver = new UrlOrSearchResolver(BrowserDefaults.SearchUrlTemplate);

        Assert.Equal(AddressKind.Search, resolver.Resolve("hello world").Kind);
    }
}
