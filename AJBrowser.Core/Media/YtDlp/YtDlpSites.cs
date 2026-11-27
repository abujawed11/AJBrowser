using System.Text.RegularExpressions;

namespace AJBrowser.Core.Media.YtDlp;

/// <summary>
/// The pages where AJBrowser offers yt-dlp by itself (plan Phase 7.5c): video pages of the big platforms, which don't
/// expose standard streams to the page. Only real video pages count (a YouTube watch page, not the home page or a
/// channel), so the film button means "there's one video here". Hosts must match exactly or as subdomains
/// ("m.youtube.com"), never by substring ("evil-youtube.com"). Any other page can still use "Try yt-dlp on this page".
/// </summary>
public static partial class YtDlpSites
{
    private static readonly (string Host, string Name, Regex? Path)[] Sites =
    [
        ("youtube.com", "YouTube", YouTubePath()),
        ("youtube-nocookie.com", "YouTube", EmbedPath()),
        ("youtu.be", "YouTube", IdPath()),
        ("facebook.com", "Facebook", FacebookPath()),
        ("fb.watch", "Facebook", IdPath()),
        ("instagram.com", "Instagram", InstagramPath()),
        ("tiktok.com", "TikTok", TikTokPath()),
        ("x.com", "X", StatusPath()),
        ("twitter.com", "X", StatusPath()),
        ("vimeo.com", "Vimeo", VimeoPath()),
        ("reddit.com", "Reddit", RedditPath()),
        ("twitch.tv", "Twitch", TwitchPath()),
        ("dailymotion.com", "Dailymotion", DailymotionPath()),
        ("dai.ly", "Dailymotion", IdPath()),
        ("bilibili.com", "Bilibili", BilibiliPath()),
    ];

    /// <summary>True for a video page of a supported platform.</summary>
    public static bool IsVideoPage(Uri? url) => SiteOf(url) is not null;

    /// <summary>"YouTube", "Instagram"…, or null when it isn't a supported video page.</summary>
    public static string? SiteOf(Uri? url)
    {
        if (url is null || !url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }

        var host = url.Host.ToLowerInvariant();
        foreach (var (site, name, path) in Sites)
        {
            if ((host == site || host.EndsWith("." + site, StringComparison.Ordinal)) && (path is null || path.IsMatch(PathAndQuery(url))))
            {
                return name;
            }
        }

        return null;
    }

    private static string PathAndQuery(Uri url) => url.AbsolutePath + url.Query;

    // /watch?v=…, /shorts/…, /live/…, /embed/…
    [GeneratedRegex(@"^/(watch\?(.*&)?v=[\w-]{6,}|shorts/[\w-]{6,}|live/[\w-]{6,}|embed/[\w-]{6,})", RegexOptions.CultureInvariant)]
    private static partial Regex YouTubePath();

    [GeneratedRegex(@"^/embed/[\w-]{6,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmbedPath();

    // youtu.be/ID, fb.watch/ID, dai.ly/ID
    [GeneratedRegex(@"^/[\w-]{4,}", RegexOptions.CultureInvariant)]
    private static partial Regex IdPath();

    [GeneratedRegex(@"^/(watch/?\?(.*&)?v=\d+|reel/\d+|[^/]+/videos/|video\.php\?|share/[vr]/\w+|videos/\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex FacebookPath();

    [GeneratedRegex(@"^/(p|reel|reels|tv)/[\w-]{5,}", RegexOptions.CultureInvariant)]
    private static partial Regex InstagramPath();

    [GeneratedRegex(@"^/(@[\w.-]+/video/\d+|t/\w+|v/\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex TikTokPath();

    [GeneratedRegex(@"^/\w{1,30}/status/\d+", RegexOptions.CultureInvariant)]
    private static partial Regex StatusPath();

    [GeneratedRegex(@"^/(\d{5,}|video/\d{5,}|channels/[\w-]+/\d{5,}|groups/[\w-]+/videos/\d{5,})", RegexOptions.CultureInvariant)]
    private static partial Regex VimeoPath();

    [GeneratedRegex(@"^/r/\w+/comments/\w+", RegexOptions.CultureInvariant)]
    private static partial Regex RedditPath();

    [GeneratedRegex(@"^/(videos/\d+|\w+/clip/[\w-]+|\w+/v/\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex TwitchPath();

    [GeneratedRegex(@"^/video/\w+", RegexOptions.CultureInvariant)]
    private static partial Regex DailymotionPath();

    [GeneratedRegex(@"^/video/(BV|av)\w+", RegexOptions.CultureInvariant)]
    private static partial Regex BilibiliPath();
}
