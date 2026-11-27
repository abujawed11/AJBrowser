using System.Globalization;
using System.Text;

namespace AJBrowser.Core.Media.YtDlp;

/// <summary>A browser cookie, as the app layer reads it from the tab's WebView2 profile.</summary>
/// <param name="IsHostOnly">Sent only to exactly <see cref="Domain"/> (no leading dot), not its subdomains.</param>
/// <param name="Expires">Null = a session cookie.</param>
public sealed record BrowserCookie(string Name, string Value, string Domain, string Path, bool IsSecure, bool IsHttpOnly, bool IsHostOnly, DateTimeOffset? Expires);

/// <summary>
/// Writes cookies in the Netscape <c>cookies.txt</c> format that yt-dlp's <c>--cookies</c> reads (plan Phase 7.5a,
/// §12). The file lives in the job's temp folder under <c>%LOCALAPPDATA%</c> (only the user, SYSTEM and administrators
/// can read it) and is deleted when the yt-dlp run ends; cookies never appear on a command line or in a log.
/// </summary>
public static class NetscapeCookieFile
{
    public const string Header = "# Netscape HTTP Cookie File";

    /// <summary>The file's text. Cookies with tabs or line breaks in any field are left out (they'd break the format).</summary>
    public static string Format(IEnumerable<BrowserCookie> cookies)
    {
        var text = new StringBuilder(Header).Append('\n').Append("# Written by AJBrowser for one yt-dlp run; deleted afterwards.\n\n");
        foreach (var c in cookies)
        {
            if (new[] { c.Name, c.Value, c.Domain, c.Path }.Any(f => f.Any(ch => ch is '\t' or '\r' or '\n')) || c.Name.Length == 0 || c.Domain.Length == 0)
            {
                continue;
            }

            var domain = c.IsHostOnly ? c.Domain.TrimStart('.') : "." + c.Domain.TrimStart('.');
            var expires = c.Expires is { } at ? Math.Max(0, at.ToUnixTimeSeconds()).ToString(CultureInfo.InvariantCulture) : "0";
            text.Append(c.IsHttpOnly ? "#HttpOnly_" : string.Empty)
                .Append(domain).Append('\t')
                .Append(c.IsHostOnly ? "FALSE" : "TRUE").Append('\t')
                .Append(c.Path.Length == 0 ? "/" : c.Path).Append('\t')
                .Append(c.IsSecure ? "TRUE" : "FALSE").Append('\t')
                .Append(expires).Append('\t')
                .Append(c.Name).Append('\t')
                .Append(c.Value).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>Writes the file (UTF-8 without BOM, which yt-dlp expects) and returns its path.</summary>
    public static string Write(string folder, IEnumerable<BrowserCookie> cookies)
    {
        Directory.CreateDirectory(folder);
        var path = System.IO.Path.Combine(folder, "cookies.txt");
        File.WriteAllText(path, Format(cookies), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    /// <summary>Deletes the file if it's there (end of a run, or leftovers at startup).</summary>
    public static void Delete(string folder)
    {
        try
        {
            File.Delete(System.IO.Path.Combine(folder, "cookies.txt"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Gone already, or in use: the startup cleanup removes it.
        }
    }
}

/// <summary>
/// Which addresses' cookies yt-dlp needs for a page (V16): the page's own (what the browser sends to it) plus, for
/// sites that sign in through another domain, that domain. Only cookies the browser would send to these addresses are
/// handed over; never the whole cookie jar.
/// </summary>
public static class YtDlpCookieScope
{
    private static readonly (string Site, string[] Extra)[] SignInDomains =
    [
        ("youtube.com", ["https://www.google.com/", "https://accounts.google.com/"]),
        ("youtu.be", ["https://www.youtube.com/", "https://www.google.com/", "https://accounts.google.com/"]),
        ("instagram.com", []),
        ("facebook.com", []),
        ("fb.watch", ["https://www.facebook.com/"]),
    ];

    public static IReadOnlyList<Uri> UrlsFor(Uri page)
    {
        var urls = new List<Uri> { new UriBuilder(page.Scheme, page.Host).Uri };
        var host = page.Host.ToLowerInvariant();
        foreach (var (site, extra) in SignInDomains)
        {
            if (host == site || host.EndsWith("." + site, StringComparison.Ordinal))
            {
                urls.AddRange(extra.Select(e => new Uri(e)));
            }
        }

        return urls.DistinctBy(u => u.AbsoluteUri).ToList();
    }
}
