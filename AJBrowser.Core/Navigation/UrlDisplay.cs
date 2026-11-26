namespace AJBrowser.Core.Navigation;

/// <summary>Formats URLs for display in the address bar when it isn't being edited.</summary>
public static class UrlDisplay
{
    /// <summary>
    /// Drops "https://" and a bare trailing "/" (so "https://example.com/" shows as "example.com").
    /// "http://" stays visible on purpose, as a hint that the connection isn't secure.
    /// Other schemes (about:, file:, data:) are shown unchanged.
    /// </summary>
    public static string ForAddressBar(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return url;
        }

        var text = url;
        const string httpsPrefix = "https://";
        if (text.StartsWith(httpsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            text = text[httpsPrefix.Length..];
        }

        if (uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 && text.EndsWith('/'))
        {
            text = text[..^1];
        }

        return text;
    }
}
