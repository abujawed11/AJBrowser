using System.Text.Json;

namespace AJBrowser.Core.Media.YtDlp;

/// <summary>
/// What yt-dlp reports about one video (<c>yt-dlp -J --no-playlist</c>, plan Phase 7.5), reduced to what the picker and
/// downloader need. The JSON comes from a website through the tool, so it's treated as untrusted: size-capped, every
/// field optional and type-checked, nothing executed or followed.
/// </summary>
public sealed record YtDlpInfo(
    string? Id,
    string Title,
    double? DurationSeconds,
    string? Uploader,
    string? Extractor,
    Uri? WebpageUrl,
    Uri? Thumbnail,
    YtDlpLiveStatus LiveStatus,
    bool IsPlaylist,
    IReadOnlyList<YtDlpFormat> Formats);

public enum YtDlpLiveStatus
{
    NotLive,

    /// <summary>Live now.</summary>
    Live,

    /// <summary>Scheduled, not started.</summary>
    Upcoming,

    /// <summary>A live stream that ended but isn't processed into a normal video yet.</summary>
    PostLive,
}

/// <summary>
/// One format yt-dlp can fetch. <see cref="VideoCodec"/>/<see cref="AudioCodec"/> are null when the format has no
/// picture / no sound (yt-dlp writes "none"). Sizes: <see cref="FileSize"/> exact, <see cref="FileSizeApprox"/> estimated.
/// </summary>
public sealed record YtDlpFormat(
    string FormatId,
    string? Extension,
    string? VideoCodec,
    string? AudioCodec,
    int? Width,
    int? Height,
    double? Fps,
    double? TotalBitrateKbps,
    double? VideoBitrateKbps,
    double? AudioBitrateKbps,
    long? FileSize,
    long? FileSizeApprox,
    string? Protocol,
    string? Language,
    int? LanguagePreference,
    string? FormatNote,
    string? DynamicRange,
    int? AudioChannels,
    bool HasDrm)
{
    public bool HasVideo => VideoCodec is not null;
    public bool HasAudio => AudioCodec is not null;
    public bool IsVideoOnly => HasVideo && !HasAudio;
    public bool IsAudioOnly => HasAudio && !HasVideo;
    public bool IsMuxed => HasVideo && HasAudio;

    /// <summary>Storyboards (thumbnail sheets) and other non-media entries.</summary>
    public bool IsJunk =>
        (!HasVideo && !HasAudio)
        || string.Equals(Protocol, "mhtml", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Extension, "mhtml", StringComparison.OrdinalIgnoreCase)
        || (FormatNote?.Contains("storyboard", StringComparison.OrdinalIgnoreCase) ?? false);
}

/// <summary>Parses <c>yt-dlp -J</c> output.</summary>
public static class YtDlpInfoParser
{
    /// <summary>A YouTube video's JSON is 0.5–3 MB; anything over this isn't one video.</summary>
    public const int MaxLength = 32 * 1024 * 1024;

    /// <summary>Parses the JSON; throws <see cref="FormatException"/> when it isn't yt-dlp video info.</summary>
    public static YtDlpInfo Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaxLength)
        {
            throw new FormatException("yt-dlp's answer is too large.");
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("yt-dlp's answer isn't video information.");
            }

            var type = String(root, "_type");
            var isPlaylist = type is "playlist" or "multi_video";
            var formats = new List<YtDlpFormat>();
            if (root.TryGetProperty("formats", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && ReadFormat(item) is { } format)
                    {
                        formats.Add(format);
                    }
                }
            }
            else if (!isPlaylist && String(root, "format_id") is not null && ReadFormat(root) is { } single)
            {
                // Sites with a single file report it at the top level.
                formats.Add(single);
            }

            var live = String(root, "live_status") switch
            {
                "is_live" => YtDlpLiveStatus.Live,
                "is_upcoming" => YtDlpLiveStatus.Upcoming,
                "post_live" => YtDlpLiveStatus.PostLive,
                _ when Bool(root, "is_live") == true => YtDlpLiveStatus.Live,
                _ => YtDlpLiveStatus.NotLive,
            };

            return new YtDlpInfo(
                String(root, "id"),
                Clip(String(root, "title") ?? String(root, "fulltitle"), 300) ?? "Video",
                Number(root, "duration") is > 0 and < 1e7 and var d ? d : null,
                Clip(String(root, "uploader") ?? String(root, "channel"), 200),
                String(root, "extractor_key") ?? String(root, "extractor"),
                Http(String(root, "webpage_url")),
                Http(String(root, "thumbnail")),
                live,
                isPlaylist,
                formats);
        }
        catch (JsonException ex)
        {
            throw new FormatException("yt-dlp's answer isn't valid JSON.", ex);
        }
    }

    /// <summary>yt-dlp prints the JSON on one line; earlier lines may be stray output. Takes the last line that starts with '{'.</summary>
    public static YtDlpInfo ParseOutput(IReadOnlyList<string> stdoutLines)
    {
        var json = stdoutLines.LastOrDefault(l => l.TrimStart().StartsWith('{'));
        return json is null ? throw new FormatException("yt-dlp didn't return video information.") : Parse(json);
    }

    private static YtDlpFormat? ReadFormat(JsonElement f)
    {
        var id = String(f, "format_id");
        if (id is null || id.Length > 100)
        {
            return null;
        }

        return new YtDlpFormat(
            id,
            String(f, "ext"),
            Codec(String(f, "vcodec")),
            Codec(String(f, "acodec")),
            Int(f, "width"),
            Int(f, "height"),
            Number(f, "fps") is > 0 and < 1000 and var fps ? fps : null,
            Positive(Number(f, "tbr")),
            Positive(Number(f, "vbr")),
            Positive(Number(f, "abr")),
            Long(f, "filesize"),
            Long(f, "filesize_approx"),
            String(f, "protocol"),
            String(f, "language") is { Length: <= 35 } language ? language : null,
            Number(f, "language_preference") is { } preference ? (int)Math.Clamp(preference, -1000, 1000) : null,
            Clip(String(f, "format_note"), 100),
            String(f, "dynamic_range"),
            Int(f, "audio_channels"),
            HasDrm(f));
    }

    /// <summary>"none" (and missing) → null: the format has no such stream.</summary>
    private static string? Codec(string? value) =>
        value is null || value.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : value;

    /// <summary>yt-dlp writes true, false or "maybe"; anything but false counts as protected.</summary>
    private static bool HasDrm(JsonElement f) =>
        f.TryGetProperty("has_drm", out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.String;

    private static string? String(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static double? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;

    private static double? Positive(double? value) => value is > 0 and < 1e7 ? value : null;

    private static int? Int(JsonElement e, string name) => Number(e, name) is > 0 and < 100_000 and var d ? (int)d : null;

    private static long? Long(JsonElement e, string name) => Number(e, name) is > 0 and < 1e13 and var d ? (long)d : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;

    private static Uri? Http(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ? uri : null;

    private static string? Clip(string? value, int max) =>
        value is null ? null : new string(value.Where(c => !char.IsControl(c)).Take(max).ToArray()).Trim() is { Length: > 0 } s ? s : null;
}
