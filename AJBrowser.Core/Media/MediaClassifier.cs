using System.Globalization;
using System.Text.RegularExpressions;

namespace AJBrowser.Core.Media;

/// <summary>What a network response is, as far as video detection is concerned (plan §6.2).</summary>
public enum MediaKind
{
    /// <summary>Not media (pages, images, scripts…), or a response we can't use (errors, redirects).</summary>
    None,

    /// <summary>A whole video or audio file (MP4, WebM…), downloadable as it is.</summary>
    Direct,

    /// <summary>An HLS playlist (.m3u8): the stream's list of pieces and qualities (downloaded in Phase 6).</summary>
    Hls,

    /// <summary>A DASH manifest (.mpd), the other common stream format (Phase 7).</summary>
    Dash,

    /// <summary>A piece of a stream (.ts, .m4s, init segments…): never shown on its own.</summary>
    Segment,
}

/// <summary>
/// One response seen in a tab, reduced to what detection needs (no bodies, no cookies). The Browser layer builds it
/// from WebView2's WebResourceResponseReceived; <see cref="ResourceContext"/> is WebView2's resource-context name
/// ("Media", "Fetch", "XmlHttpRequest", "Document", "Image"…).
/// </summary>
public sealed record MediaObservation(
    Uri Url,
    int StatusCode,
    string? ContentType,
    long? ContentLength,
    string? ContentRange,
    string? ResourceContext);

/// <summary>The classifier's verdict. <see cref="Container"/> is a short format name ("MP4", "WebM"…), when known.</summary>
public sealed record MediaClassification(MediaKind Kind, string? Container = null, bool IsAudioOnly = false)
{
    public static readonly MediaClassification NotMedia = new(MediaKind.None);
}

/// <summary>
/// Decides what a response is: content type first, file extension as a fallback, and rules that keep stream pieces
/// out. Content types are unreliable in practice (spike F1): HLS playlists arrive as <c>audio/mpegurl</c> or untyped,
/// TS pieces as <c>application/octet-stream</c>, DASH pieces as <c>video/mp4</c>. A pure function, unit-tested.
/// </summary>
public static partial class MediaClassifier
{
    private static readonly HashSet<string> HlsTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/vnd.apple.mpegurl", "application/x-mpegurl", "audio/mpegurl", "audio/x-mpegurl", "application/mpegurl",
    };

    private static readonly HashSet<string> DashTypes = new(StringComparer.OrdinalIgnoreCase) { "application/dash+xml" };

    private static readonly HashSet<string> SegmentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "video/mp2t", "video/iso.segment", "audio/iso.segment", "audio/aac", "audio/x-aac", "text/vtt",
    };

    private static readonly HashSet<string> SegmentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".m4s", ".m4f", ".cmfv", ".cmfa", ".aac", ".vtt", ".webvtt", ".key",
    };

    /// <summary>Content type → (container, audio only).</summary>
    private static readonly Dictionary<string, (string Container, bool Audio)> DirectTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["video/mp4"] = ("MP4", false), ["video/x-m4v"] = ("MP4", false), ["video/webm"] = ("WebM", false),
        ["video/quicktime"] = ("MOV", false), ["video/x-matroska"] = ("MKV", false), ["video/ogg"] = ("OGG", false),
        ["video/3gpp"] = ("3GP", false), ["audio/mp4"] = ("M4A", true), ["audio/x-m4a"] = ("M4A", true),
        ["audio/mpeg"] = ("MP3", true), ["audio/mp3"] = ("MP3", true), ["audio/webm"] = ("WebM", true),
        ["audio/ogg"] = ("OGG", true), ["audio/opus"] = ("OPUS", true), ["audio/wav"] = ("WAV", true), ["audio/flac"] = ("FLAC", true),
    };

    /// <summary>Extension → (container, audio only), used when the content type says nothing useful.</summary>
    private static readonly Dictionary<string, (string Container, bool Audio)> DirectExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = ("MP4", false), [".m4v"] = ("MP4", false), [".webm"] = ("WebM", false), [".mov"] = ("MOV", false),
        [".mkv"] = ("MKV", false), [".ogv"] = ("OGG", false), [".3gp"] = ("3GP", false), [".m4a"] = ("M4A", true),
        [".mp3"] = ("MP3", true), [".oga"] = ("OGG", true), [".ogg"] = ("OGG", true), [".opus"] = ("OPUS", true),
        [".wav"] = ("WAV", true), [".flac"] = ("FLAC", true),
    };

    /// <summary>Content types that tell us nothing, so the extension decides.</summary>
    private static readonly HashSet<string> VagueTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "application/octet-stream", "binary/octet-stream", "application/binary", "application/force-download", "application/x-download",
    };

    /// <summary>Resource contexts that are never media (cheap early exit for the bulk of requests).</summary>
    private static readonly HashSet<string> NonMediaContexts = new(StringComparer.OrdinalIgnoreCase)
    {
        "Image", "Stylesheet", "Script", "Font", "Manifest", "SignedExchange", "Ping", "CspViolationReport", "TextTrack", "EventSource", "Websocket",
    };

    public static MediaClassification Classify(MediaObservation observation)
    {
        if (observation.StatusCode is not (200 or 206) || !IsHttp(observation.Url)
            || (observation.ResourceContext is { } context && NonMediaContexts.Contains(context)))
        {
            return MediaClassification.NotMedia;
        }

        var type = MediaTypeOf(observation.ContentType);
        var path = observation.Url.AbsolutePath;
        var extension = ExtensionOf(path);

        if (HlsTypes.Contains(type) || extension.Equals(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return new MediaClassification(MediaKind.Hls, "HLS");
        }

        if (DashTypes.Contains(type) || extension.Equals(".mpd", StringComparison.OrdinalIgnoreCase))
        {
            return new MediaClassification(MediaKind.Dash, "DASH");
        }

        if (SegmentTypes.Contains(type) || SegmentExtensions.Contains(extension) || LooksLikeSegmentName(path))
        {
            return new MediaClassification(MediaKind.Segment);
        }

        if (DirectTypes.TryGetValue(type, out var direct))
        {
            return new MediaClassification(MediaKind.Direct, direct.Container, direct.Audio);
        }

        if (VagueTypes.Contains(type) && DirectExtensions.TryGetValue(extension, out var byExtension))
        {
            return new MediaClassification(MediaKind.Direct, byExtension.Container, byExtension.Audio);
        }

        return MediaClassification.NotMedia;
    }

    /// <summary>
    /// The full size of the file behind a response: the total of <c>Content-Range: bytes a-b/TOTAL</c> on a 206 (a
    /// player asking for a part), else <c>Content-Length</c> on a 200. Null when unknown ("*" or missing).
    /// </summary>
    public static long? TotalSize(MediaObservation observation)
    {
        if (observation.StatusCode == 206)
        {
            var range = observation.ContentRange;
            var slash = range?.LastIndexOf('/') ?? -1;
            return slash >= 0 && long.TryParse(range![(slash + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var total) && total > 0
                ? total
                : null;
        }

        return observation.ContentLength is > 0 ? observation.ContentLength : null;
    }

    /// <summary>
    /// The identity of a media file for de-duplication: scheme, host and path, without the query. CDNs rotate tokens in
    /// the query, and a player asks for many byte ranges of the same file; all of that is one video.
    /// </summary>
    public static string KeyOf(Uri url) =>
        url.GetComponents(UriComponents.Scheme | UriComponents.Host | UriComponents.Port | UriComponents.Path, UriFormat.UriEscaped);

    private static bool IsHttp(Uri url) => url.IsAbsoluteUri && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp);

    private static string MediaTypeOf(string? contentType) =>
        string.IsNullOrWhiteSpace(contentType) ? string.Empty : contentType.Split(';')[0].Trim();

    private static string ExtensionOf(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        var name = lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name[dot..] : string.Empty;
    }

    /// <summary>
    /// File names streaming players use for pieces even when served as video/mp4: "init.mp4", "init-v1.m4s",
    /// "seg-12.m4v", "segment_3.mp4", "chunk-0001.m4a", "frag5.mp4", "fragment-7.mp4".
    /// </summary>
    private static bool LooksLikeSegmentName(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        var name = lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
        return SegmentNameRegex().IsMatch(name);
    }

    [GeneratedRegex(@"^(init([-_.].*)?\.(mp4|m4v|m4a|m4s|webm)|.*(^|[-_.])(seg|segment|chunk|frag|fragment)[-_]?\d+.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SegmentNameRegex();
}
