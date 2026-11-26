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

    /// <summary>Redacts sensitive header values, bearer tokens and URLs found anywhere in free text.</summary>
    public static string Text(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var result = SensitiveHeaderRegex().Replace(text, m => $"{m.Groups["name"].Value}{m.Groups["sep"].Value}{Marker}");
        result = BearerRegex().Replace(result, "Bearer " + Marker);
        result = UrlRegex().Replace(result, m => Url(m.Value));
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
