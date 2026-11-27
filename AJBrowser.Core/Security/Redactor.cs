using System.Text.RegularExpressions;

namespace AJBrowser.Core.Security;

/// <summary>
/// Removes secrets before text reaches logs or persisted history: URL query strings and fragments
/// (signed media URLs, OAuth tokens), user-info in URLs, and cookie/authorization header values.
/// Everything AJBrowser logs must pass through here (AppLogger does this automatically).
/// </summary>
public static partial class Redactor
{
    public const string Marker = "[redacted]";

    private static readonly HashSet<string> HierarchicalSchemes =
        new(StringComparer.OrdinalIgnoreCase) { "http", "https", "ws", "wss", "ftp", "file" };

    /// <summary>Returns scheme, host, port and path only; query and fragment are replaced by markers.</summary>
    public static string Url(Uri? uri)
    {
        if (uri is null)
        {
            return "[no-url]";
        }

        if (!uri.IsAbsoluteUri)
        {
            return "[relative-url]";
        }

        if (!HierarchicalSchemes.Contains(uri.Scheme))
        {
            // data:, blob:, javascript:, mailto: ... the whole remainder may be sensitive.
            return $"{uri.Scheme}:{Marker}";
        }

        // SchemeAndServer excludes user-info and omits default ports.
        var result = uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);

        if (uri.Query.Length > 1)
        {
            result += "?" + Marker;
        }

        if (uri.Fragment.Length > 1)
        {
            result += "#" + Marker;
        }

        return result;
    }

    public static string Url(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "[no-url]";
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Url(uri) : "[invalid-url]";
    }

    /// <summary>
    /// For media addresses (logs, the video picker's tooltip, download history): like <see cref="Url(Uri?)"/>, and also
    /// replaces long or random-looking <b>path</b> segments with "[id]", because CDNs put signed tokens in the path
    /// (spike F12: <c>/live/use1/d17_Cixj…/master.m3u8</c>). The last segment keeps its extension: "[id].m3u8".
    /// </summary>
    public static string MediaUrl(Uri? uri)
    {
        var basic = Url(uri);
        if (uri is null || !uri.IsAbsoluteUri || !HierarchicalSchemes.Contains(uri.Scheme))
        {
            return basic;
        }

        var segments = uri.AbsolutePath.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            var isLast = i == segments.Length - 1;
            var dot = isLast ? segment.LastIndexOf('.') : -1;
            var stem = dot > 0 ? segment[..dot] : segment;
            if (IsOpaque(Uri.UnescapeDataString(stem)))
            {
                segments[i] = "[id]" + (dot > 0 ? segment[dot..] : string.Empty);
            }
        }

        // SchemeAndServer leaves out user info, like Url() does.
        var result = uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped) + string.Join('/', segments);

        if (uri.Query.Length > 1)
        {
            result += "?" + Marker;
        }

        if (uri.Fragment.Length > 1)
        {
            result += "#" + Marker;
        }

        return result;
    }

    public static string MediaUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? MediaUrl(uri) : Url(url);

    /// <summary>
    /// A token rather than a name: 40+ characters, or 16+ with at least 2 digits and 2 letters (UUIDs, hashes, base64
    /// keys). Ordinary words and slugs ("my-holiday-trip", "2024", "video_1080p") stay readable.
    /// </summary>
    private static bool IsOpaque(string segment)
    {
        if (segment.Length >= 40)
        {
            return true;
        }

        if (segment.Length < 16)
        {
            return false;
        }

        var digits = segment.Count(char.IsAsciiDigit);
        var letters = segment.Count(char.IsAsciiLetter);
        return digits >= 2 && letters >= 2;
    }

    /// <summary>
    /// Redacts sensitive header values, bearer tokens and URLs found anywhere in free text. URLs go through
    /// <see cref="MediaUrl"/>: besides the query, long or opaque path segments (tokens such as signed JWTs that CDNs and
    /// file hosts put in the path, plan F12) are replaced too.
    /// </summary>
    public static string Text(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var result = SensitiveHeaderRegex().Replace(text, m => $"{m.Groups["name"].Value}{m.Groups["sep"].Value}{Marker}");
        result = BearerRegex().Replace(result, "Bearer " + Marker);
        result = UrlRegex().Replace(result, m => MediaUrl(m.Value));
        return result;
    }

    [GeneratedRegex(@"\b(?<name>cookie|set-cookie|authorization|proxy-authorization|x-api-key)(?<sep>\s*[:=]\s*)[^\r\n]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveHeaderRegex();

    [GeneratedRegex(@"\bbearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"\b(?:https?|wss?|ftp|file|blob|data|javascript):[^\s""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();
}
