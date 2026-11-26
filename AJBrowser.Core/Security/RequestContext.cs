namespace AJBrowser.Core.Security;

/// <summary>
/// The request headers AJBrowser repeats when it downloads a detected video itself, so the server sees the same
/// kind of request the page's player made (plan §6.2, §5.8). Only an <b>allowlist</b> is copied from the observed
/// request: <c>Referer</c>, <c>Origin</c>, <c>User-Agent</c>, <c>Accept-Language</c>. Never <c>Cookie</c> or
/// <c>Authorization</c>: cookies are asked from the tab's own profile at download start, for that exact URL
/// (<see cref="ToHeaders"/>). Memory only: never logged or saved.
/// </summary>
public sealed class RequestContext
{
    /// <summary>Header values longer than this are dropped (a sane request never needs more).</summary>
    public const int MaxValueLength = 2048;

    private static readonly string[] Allowed = ["Referer", "Origin", "User-Agent", "Accept-Language"];

    private readonly Dictionary<string, string> _headers;

    private RequestContext(Dictionary<string, string> headers) => _headers = headers;

    /// <summary>The copied headers (allowlisted names, canonical spelling).</summary>
    public IReadOnlyDictionary<string, string> Headers => _headers;

    /// <summary>
    /// Copies the allowlisted headers from the request the page made. If it had no Referer, the page's origin is used
    /// (what browsers send by default to other sites), never the full page address.
    /// </summary>
    public static RequestContext FromObservedRequest(IEnumerable<KeyValuePair<string, string>> requestHeaders, Uri? pageUrl)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in requestHeaders)
        {
            var canonical = Array.Find(Allowed, a => a.Equals(name?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (canonical is not null && IsSafeValue(value))
            {
                headers[canonical] = value.Trim();
            }
        }

        if (!headers.ContainsKey("Referer") && pageUrl is { IsAbsoluteUri: true } page
            && (page.Scheme == Uri.UriSchemeHttps || page.Scheme == Uri.UriSchemeHttp))
        {
            headers["Referer"] = page.GetLeftPart(UriPartial.Authority) + "/";
        }

        return new RequestContext(headers);
    }

    /// <summary>
    /// The headers for the download request. <paramref name="cookieHeader"/> is what the tab's profile would send to
    /// the media URL right now ("a=1; b=2"), fetched at download start; null or empty sends none.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToHeaders(string? cookieHeader)
    {
        var result = new Dictionary<string, string>(_headers, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(cookieHeader) && IsSafeValue(cookieHeader, maxLength: 8192))
        {
            result["Cookie"] = cookieHeader.Trim();
        }

        return result;
    }

    /// <summary>No line breaks (header injection), no control characters, not too long.</summary>
    private static bool IsSafeValue(string? value, int maxLength = MaxValueLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength && !value.Any(c => c < 32 && c != '\t');
}
