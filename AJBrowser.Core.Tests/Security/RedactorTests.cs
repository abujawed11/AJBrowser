using AJBrowser.Core.Security;

namespace AJBrowser.Core.Tests.Security;

public class RedactorTests
{
    [Theory]
    [InlineData("https://example.com/", "https://example.com/")]
    [InlineData("https://example.com/video.mp4", "https://example.com/video.mp4")]
    [InlineData("https://cdn.example.com/v.mp4?Expires=123&Signature=abc", "https://cdn.example.com/v.mp4?[redacted]")]
    [InlineData("https://example.com/cb#access_token=secret", "https://example.com/cb#[redacted]")]
    [InlineData("https://example.com/a?x=1#y", "https://example.com/a?[redacted]#[redacted]")]
    [InlineData("https://user:pass@example.com/private", "https://example.com/private")]
    [InlineData("https://example.com:8443/p?q=1", "https://example.com:8443/p?[redacted]")]
    [InlineData("https://example.com:443/p", "https://example.com/p")]
    [InlineData("http://localhost:5000/", "http://localhost:5000/")]
    public void Url_keeps_origin_and_path_only(string input, string expected)
    {
        Assert.Equal(expected, Redactor.Url(input));
    }

    [Theory]
    [InlineData("blob:https://example.com/550e8400-e29b-41d4-a716-446655440000", "blob:[redacted]")]
    [InlineData("data:text/plain;base64,SGVsbG8=", "data:[redacted]")]
    [InlineData("javascript:alert(document.cookie)", "javascript:[redacted]")]
    [InlineData("mailto:someone@example.com", "mailto:[redacted]")]
    public void Url_hides_entire_body_of_non_hierarchical_schemes(string input, string expected)
    {
        Assert.Equal(expected, Redactor.Url(input));
    }

    [Theory]
    [InlineData(null, "[no-url]")]
    [InlineData("", "[no-url]")]
    [InlineData("not a url", "[invalid-url]")]
    public void Url_handles_missing_or_invalid_input(string? input, string expected)
    {
        Assert.Equal(expected, Redactor.Url(input));
    }

    [Fact]
    public void Url_of_relative_uri_is_not_echoed()
    {
        Assert.Equal("[relative-url]", Redactor.Url(new Uri("/path?token=1", UriKind.Relative)));
    }

    [Theory]
    [InlineData("Cookie: session=abc123; theme=dark", "Cookie: [redacted]")]
    [InlineData("set-cookie: id=42; HttpOnly", "set-cookie: [redacted]")]
    [InlineData("Authorization: Basic dXNlcjpwYXNz", "Authorization: [redacted]")]
    [InlineData("Proxy-Authorization: Negotiate abc", "Proxy-Authorization: [redacted]")]
    [InlineData("x-api-key=supersecret", "x-api-key=[redacted]")]
    public void Text_redacts_sensitive_header_values(string input, string expected)
    {
        Assert.Equal(expected, Redactor.Text(input));
    }

    [Fact]
    public void Text_redacts_bearer_tokens()
    {
        Assert.Equal("token was Bearer [redacted] here", Redactor.Text("token was Bearer eyJhbGciOi.payload.sig here"));
    }

    [Fact]
    public void Text_redacts_urls_embedded_in_messages()
    {
        var message = "Download failed for https://cdn.example.com/v.mp4?sig=SECRET (403); page https://example.com/watch";

        var result = Redactor.Text(message);

        Assert.Equal("Download failed for https://cdn.example.com/v.mp4?[redacted] (403); page https://example.com/watch", result);
        Assert.DoesNotContain("SECRET", result);
    }

    [Fact]
    public void Text_redacts_header_lines_but_keeps_other_lines()
    {
        var message = "Request headers:\r\nAccept: */*\r\nCookie: a=1\r\nReferer: https://example.com/page?id=9";

        var result = Redactor.Text(message);

        Assert.Equal("Request headers:\r\nAccept: */*\r\nCookie: [redacted]\r\nReferer: https://example.com/page?[redacted]", result);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("plain message", "plain message")]
    public void Text_passes_through_harmless_text(string? input, string expected)
    {
        Assert.Equal(expected, Redactor.Text(input));
    }
}
