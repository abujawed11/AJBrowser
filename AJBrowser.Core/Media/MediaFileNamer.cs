using AJBrowser.Core.Downloads;

namespace AJBrowser.Core.Media;

/// <summary>Suggests a file name for a detected video: "Page title 720p.mp4" (plan Phase 5).</summary>
public static class MediaFileNamer
{
    /// <summary>Page titles are cut to this many characters so the name stays manageable.</summary>
    public const int MaxTitleLength = 100;

    /// <summary>
    /// The page title (or the file's own name when there's no useful title), the quality when known ("720p", or
    /// "audio"), and an extension from the format or the URL. Always a safe Windows file name.
    /// </summary>
    public static string Suggest(DetectedMedia media, string? pageTitle)
    {
        var urlName = Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(media.Url.AbsolutePath));
        var title = string.IsNullOrWhiteSpace(pageTitle) || pageTitle.Trim().StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? urlName
            : pageTitle.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            title = media.IsAudioOnly ? "audio" : "video";
        }

        if (title.Length > MaxTitleLength)
        {
            title = title[..MaxTitleLength].TrimEnd();
        }

        var quality = media.IsAudioOnly ? null : media.Height is > 0 ? $"{media.Height}p" : null;
        var name = quality is null || title.EndsWith(quality, StringComparison.OrdinalIgnoreCase) ? title : $"{title} {quality}";
        return FileNamer.Sanitize(name + ExtensionFor(media));
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
}
