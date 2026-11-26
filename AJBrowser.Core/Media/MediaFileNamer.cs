using System.Text.RegularExpressions;
using AJBrowser.Core.Downloads;

namespace AJBrowser.Core.Media;

/// <summary>
/// Names a detected video (plan Phase 5). Best first:
/// <list type="number">
/// <item>the name the <b>server</b> gave (<c>Content-Disposition</c>), used as it is;</item>
/// <item>the file name in the <b>address</b>, if it reads like a real name ("Pritam and Pedro S01E03.mkv",
/// "my-holiday-trip.mp4") rather than a token ("d17_CixjLkPq2bX9", "3f2a9c4e-…") or a generic word ("video", "master");</item>
/// <item>the <b>page title</b> plus the quality: "My trip 720p.mp4";</item>
/// <item>whatever the address has, else "video".</item>
/// </list>
/// Always a safe Windows file name with an extension.
/// </summary>
public static partial class MediaFileNamer
{
    /// <summary>Page titles are cut to this many characters so the name stays manageable.</summary>
    public const int MaxTitleLength = 100;

    /// <summary>Names that say nothing about the content (players, CDNs and file hosts use them a lot).</summary>
    private static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "video", "videos", "audio", "media", "file", "download", "stream", "play", "playback", "videoplayback", "index",
        "master", "playlist", "manifest", "source", "movie", "clip", "content", "get", "watch", "view", "embed", "blob",
    };

    public static string Suggest(DetectedMedia media, string? pageTitle)
    {
        // 1. The server's own name for the file.
        if (media.ServerFileName is { } serverName && IsMeaningful(Path.GetFileNameWithoutExtension(serverName), lenient: true))
        {
            return WithExtension(serverName, media);
        }

        // 2. A real name in the address.
        var urlName = Uri.UnescapeDataString(Path.GetFileName(media.Url.AbsolutePath));
        var urlStem = Path.GetFileNameWithoutExtension(urlName);
        if (IsMeaningful(urlStem, lenient: false))
        {
            return WithExtension(urlName, media);
        }

        // 3. The page title and the quality.
        var title = string.IsNullOrWhiteSpace(pageTitle) || pageTitle.Trim().StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? null
            : pageTitle.Trim();
        if (title is not null)
        {
            if (title.Length > MaxTitleLength)
            {
                title = title[..MaxTitleLength].TrimEnd();
            }

            var quality = media.IsAudioOnly ? null : media.Height is > 0 ? $"{media.Height}p" : null;
            var name = quality is null || title.EndsWith(quality, StringComparison.OrdinalIgnoreCase) ? title : $"{title} {quality}";
            return FileNamer.Sanitize(name + ExtensionFor(media));
        }

        // 4. Whatever is left.
        var fallback = string.IsNullOrWhiteSpace(urlStem) ? (media.IsAudioOnly ? "audio" : "video") : urlStem;
        return FileNamer.Sanitize(fallback + ExtensionFor(media));
    }

    /// <summary>
    /// Reads like a real name: has a space, or at least two word-like parts of 3+ letters ("my-holiday-trip"), and
    /// isn't a generic word. <paramref name="lenient"/> (the server's own name) also accepts a single non-generic word.
    /// </summary>
    public static bool IsMeaningful(string? stem, bool lenient)
    {
        if (string.IsNullOrWhiteSpace(stem) || GenericNames.Contains(stem.Trim()))
        {
            return false;
        }

        var words = WordRegex().Matches(stem).Count;
        if (stem.Trim().Contains(' ') && words >= 1)
        {
            return true;
        }

        return words >= 2 || (lenient && words >= 1);
    }

    /// <summary>Keeps the name's own extension if it has one that fits a media file, else adds one from the format.</summary>
    private static string WithExtension(string name, DetectedMedia media)
    {
        var extension = Path.GetExtension(name);
        var hasMediaExtension = extension.Length is > 1 and <= 6 && extension[1..].All(char.IsAsciiLetterOrDigit) && !extension[1..].All(char.IsAsciiDigit);
        return FileNamer.Sanitize(hasMediaExtension ? name : name + ExtensionFor(media));
    }

    private static string ExtensionFor(DetectedMedia media)
    {
        var fromUrl = Path.GetExtension(media.Url.AbsolutePath);
        return media.Container switch
        {
            "MP4" => fromUrl.Equals(".m4v", StringComparison.OrdinalIgnoreCase) ? ".m4v" : ".mp4",
            "WebM" => media.IsAudioOnly ? ".weba" : ".webm",
            "MOV" => ".mov",
            "MKV" => ".mkv",
            "OGG" => media.IsAudioOnly ? ".ogg" : ".ogv",
            "3GP" => ".3gp",
            "M4A" => ".m4a",
            "MP3" => ".mp3",
            "OPUS" => ".opus",
            "WAV" => ".wav",
            "FLAC" => ".flac",
            _ => fromUrl.Length is > 1 and <= 6 ? fromUrl.ToLowerInvariant() : media.IsAudioOnly ? ".m4a" : ".mp4",
        };
    }

    /// <summary>A run of 3+ letters standing on its own (between spaces, dots, dashes, underscores or the ends).</summary>
    [GeneratedRegex(@"(?<![\p{L}\p{N}])\p{L}{3,}(?![\p{L}\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();
}
