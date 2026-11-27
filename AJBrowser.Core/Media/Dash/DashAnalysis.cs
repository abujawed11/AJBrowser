using System.Globalization;
using AJBrowser.Core.Downloads;
using AJBrowser.Core.Media.Hls;

namespace AJBrowser.Core.Media.Dash;

/// <summary>Whether AJBrowser can download a DASH video (plan Phase 7).</summary>
public enum DashSupport
{
    /// <summary>Static, unprotected, one period, with at least one picture track: downloadable.</summary>
    Supported,

    /// <summary><c>ContentProtection</c> (Widevine, PlayReady, ClearKey…): never supported.</summary>
    Protected,

    /// <summary><c>type="dynamic"</c>: a live stream, not supported yet.</summary>
    Live,

    /// <summary>Several periods (chapters or inserted ads): not supported yet (plan §9).</summary>
    MultiPeriod,

    /// <summary>No usable picture track.</summary>
    Empty,
}

/// <summary>Decisions about a DASH manifest. Pure functions, unit-tested; labels and codecs reuse the HLS rules.</summary>
public static class DashAnalysis
{
    /// <summary>DRM wins over live, live over multi-period.</summary>
    public static DashSupport Classify(DashManifest manifest)
    {
        if (manifest.IsProtected)
        {
            return DashSupport.Protected;
        }

        if (manifest.IsDynamic)
        {
            return DashSupport.Live;
        }

        if (manifest.PeriodCount > 1)
        {
            return DashSupport.MultiPeriod;
        }

        return manifest.Video.Any(v => v.Segments.Count > 0) ? DashSupport.Supported : DashSupport.Empty;
    }

    /// <summary>The user-facing sentence for a support state (null when supported).</summary>
    public static string? Explain(DashSupport support) => support switch
    {
        DashSupport.Protected => "Protected (DRM): can't be downloaded",
        DashSupport.Live => "Live stream: not supported yet",
        DashSupport.MultiPeriod => "This video is made of several parts: not supported yet",
        DashSupport.Empty => "No downloadable video in this stream",
        _ => null,
    };

    /// <summary>The video's length: the manifest's own, else the sum of a track's pieces.</summary>
    public static double? Duration(DashManifest manifest) =>
        manifest.DurationSeconds is > 0
            ? manifest.DurationSeconds
            : manifest.Video.Select(v => v.SegmentDurationSeconds).DefaultIfEmpty(0).Max() is > 0 and var sum ? sum : null;

    /// <summary>Picture tracks, best first: height, then bitrate.</summary>
    public static IReadOnlyList<DashRepresentation> SortVideoBestFirst(DashManifest manifest) =>
        manifest.Video.Where(v => v.Segments.Count > 0)
            .OrderByDescending(v => v.Height ?? 0).ThenByDescending(v => v.Bandwidth).ToList();

    /// <summary>
    /// The audio track to pair with <paramref name="video"/>: one in <paramref name="preferredLanguage"/> (by its
    /// primary subtag, so "en" matches "en-US"), else the manifest's "main" role, else any; among those, one whose codec
    /// fits the same container as the picture (AAC for H.264, Opus for VP9), then the highest bitrate. Null when there
    /// is no separate audio (silent video, or the picture track already has sound).
    /// </summary>
    public static DashRepresentation? PickAudio(DashManifest manifest, DashRepresentation? video, string? preferredLanguage)
    {
        if (video?.Kind == DashTrackKind.Muxed)
        {
            return null;
        }

        var audio = manifest.Audio.Where(a => a.Segments.Count > 0).ToList();
        if (audio.Count == 0)
        {
            return null;
        }

        var wanted = PrimaryLanguage(preferredLanguage);
        var byLanguage = wanted is null ? [] : audio.Where(a => PrimaryLanguage(a.Language) == wanted).ToList();
        var candidates = byLanguage.Count > 0 ? byLanguage
            : audio.Where(a => a.IsMainRole).ToList() is { Count: > 0 } main ? main
            : audio;

        var videoContainer = video is null ? null : SelectContainer(video, null).Container;
        return candidates
            .OrderByDescending(a => videoContainer is not null && SelectContainer(video!, a).Container == videoContainer)
            .ThenByDescending(a => a.Bandwidth)
            .First();
    }

    /// <summary>
    /// The output container for a picture track plus an optional audio track (plan §6.4). When the manifest doesn't
    /// list codecs, the MIME type decides (<c>video/webm</c> → WebM, otherwise MP4).
    /// </summary>
    public static (string Container, string Reason) SelectContainer(DashRepresentation video, DashRepresentation? audio)
    {
        var codecs = string.Join(",", new[] { video.Codecs, audio?.Codecs }.Where(c => !string.IsNullOrWhiteSpace(c)));
        if (codecs.Length == 0)
        {
            var webm = IsWebm(video) && (audio is null || IsWebm(audio));
            return webm
                ? ("WebM", "The stream doesn't list its codecs; its files are WebM.")
                : ("MP4", "The stream doesn't list its codecs; its files are MP4.");
        }

        return HlsAnalysis.SelectContainer(codecs);
    }

    private static bool IsWebm(DashRepresentation track) =>
        string.Equals(track.MimeType?.Split('/').LastOrDefault(), "webm", StringComparison.OrdinalIgnoreCase);

    /// <summary>"1080p · 4.8 Mbps · H.264 · ≈ 687 MB est." (bitrate × duration; the audio track's bitrate is added when given).</summary>
    public static string VideoLabel(DashRepresentation video, double? durationSeconds, DashRepresentation? audio = null, IFormatProvider? culture = null)
    {
        var parts = new List<string>();
        if (video.Height is > 0)
        {
            parts.Add($"{video.Height}p");
        }

        if (video.FrameRate is > 31)
        {
            parts.Add(string.Format(CultureInfo.InvariantCulture, "{0:0}fps", video.FrameRate));
        }

        if (video.Bandwidth > 0)
        {
            parts.Add(HlsAnalysis.Bitrate(video.Bandwidth));
        }

        if (HlsAnalysis.CodecLabel(video.Codecs) is { } codecs)
        {
            parts.Add(codecs);
        }

        var bits = video.Bandwidth + (audio?.Bandwidth ?? 0);
        if (durationSeconds is { } seconds && HlsAnalysis.EstimateBytes(bits, seconds) is { } bytes)
        {
            parts.Add($"≈ {DownloadText.Size(bytes, culture)} est.");
        }

        return parts.Count == 0 ? "Unknown quality" : string.Join(" · ", parts);
    }

    /// <summary>"English · AAC · 128 kbps · 5.1" — the label or language, codec, bitrate, channels when given.</summary>
    public static string AudioLabel(DashRepresentation audio)
    {
        var parts = new List<string>();
        if (audio.Label is { } label)
        {
            parts.Add(label);
        }
        else if (LanguageName(audio.Language) is { } language)
        {
            parts.Add(language);
        }

        if (HlsAnalysis.CodecLabel(audio.Codecs) is { } codec)
        {
            parts.Add(codec);
        }

        if (audio.Bandwidth > 0)
        {
            parts.Add(HlsAnalysis.Bitrate(audio.Bandwidth));
        }

        if (audio.AudioChannels is { } channels)
        {
            parts.Add(channels switch { 1 => "mono", 2 => "stereo", 6 => "5.1", 8 => "7.1", _ => $"{channels} ch" });
        }

        return parts.Count == 0 ? "Audio" : string.Join(" · ", parts);
    }

    /// <summary>"en-US" → "English"; unknown or invalid tags are shown as they are; "und"/empty → null.</summary>
    public static string? LanguageName(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || tag.Equals("und", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(PrimaryLanguage(tag)!);
            return culture.ThreeLetterISOLanguageName == "ivl" || culture.EnglishName.StartsWith("Unknown", StringComparison.Ordinal)
                ? tag
                : culture.EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return tag;
        }
    }

    private static string? PrimaryLanguage(string? tag) =>
        string.IsNullOrWhiteSpace(tag) ? null : tag.Split('-', '_')[0].Trim().ToLowerInvariant();
}
