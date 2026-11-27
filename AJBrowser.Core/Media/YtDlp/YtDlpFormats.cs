using System.Globalization;
using AJBrowser.Core.Downloads;
using AJBrowser.Core.Media.Dash;
using AJBrowser.Core.Media.Hls;

namespace AJBrowser.Core.Media.YtDlp;

/// <summary>
/// One row of the yt-dlp quality list: "1080p · 60fps · H.264 · MP4 · ≈ 412 MB". <see cref="FormatSelector"/> is the
/// exact yt-dlp <c>-f</c> value ("137+140", "18", "140"); <see cref="MergeFormat"/> is set when picture and sound are
/// joined (FFmpeg) and decides the file type.
/// </summary>
public sealed record YtDlpQualityRow(
    string Label,
    string FormatSelector,
    string? MergeFormat,
    string OutputExtension,
    int? Height,
    double? Fps,
    long? EstimatedBytes,
    bool IsEstimate,
    bool IsAudioOnly,
    string? VideoFormatId,
    string? AudioFormatId)
{
    /// <summary>The label is what screen readers announce for the list item.</summary>
    public override string ToString() => Label;
}

/// <summary>A sound track to choose instead of the automatic one (several languages).</summary>
public sealed record YtDlpAudioChoice(string Label, string FormatId, string? Language)
{
    public override string ToString() => Label;
}

/// <summary>What the picker offers for a video: rows best first, sound choices, or why nothing can be downloaded.</summary>
public sealed record YtDlpChoices(IReadOnlyList<YtDlpQualityRow> Rows, IReadOnlyList<YtDlpAudioChoice> AudioTracks, string? Problem, string? Note)
{
    public bool CanDownload => Problem is null && Rows.Count > 0;
}

/// <summary>Turns yt-dlp's format list into the picker's quality rows (plan Phase 7.5a). Pure, unit-tested.</summary>
public static class YtDlpFormats
{
    public const string NeedFfmpegNote = "Install FFmpeg (Settings → Downloads) for higher qualities: this site keeps picture and sound separate above this.";
    public const string NeedFfmpegOnlyNote = "This site keeps picture and sound separate: install FFmpeg (Settings → Downloads) to download it.";

    /// <summary>
    /// Builds the rows: one per height (and frame rate above 30), best first. With FFmpeg each row pairs the best
    /// picture-only format (H.264, then AV1, then VP9, then bitrate: the most playable first) with the best sound for it
    /// (<see cref="PickAudio"/>), or uses a format that already has both when no picture-only one exists at that height.
    /// Without FFmpeg only formats with both are offered. Plus "Audio only" rows (M4A and Opus). Formats marked DRM,
    /// storyboards and thumbnail sheets are never offered.
    /// </summary>
    public static YtDlpChoices Build(YtDlpInfo info, bool ffmpegAvailable, string? preferredLanguage)
    {
        if (info.IsPlaylist)
        {
            return Refuse("This page is a playlist. Open a single video, then try again.");
        }

        switch (info.LiveStatus)
        {
            case YtDlpLiveStatus.Live:
                return Refuse("Live stream: not supported yet");
            case YtDlpLiveStatus.Upcoming:
                return Refuse("This video hasn't started yet.");
            case YtDlpLiveStatus.PostLive:
                return Refuse("This live stream just ended and isn't ready to download yet. Try again later.");
        }

        var usable = info.Formats.Where(f => !f.IsJunk).Select(Normalize).ToList();
        if (usable.Count > 0 && usable.All(f => f.HasDrm))
        {
            return Refuse("Protected (DRM): can't be downloaded");
        }

        usable = usable.Where(f => !f.HasDrm).ToList();
        var audio = usable.Where(f => f.IsAudioOnly).ToList();
        var videoOnly = ffmpegAvailable ? usable.Where(f => f.IsVideoOnly && f.Height is > 0).ToList() : [];
        var muxed = usable.Where(f => f.IsMuxed).ToList();

        var rows = new List<YtDlpQualityRow>();
        var buckets = videoOnly.Concat(muxed).Where(f => f.Height is > 0)
            .GroupBy(f => (Height: f.Height!.Value, Fps: FpsBucket(f.Fps)))
            .OrderByDescending(g => g.Key.Height).ThenByDescending(g => g.Key.Fps);
        foreach (var bucket in buckets)
        {
            var picture = bucket.Where(f => f.IsVideoOnly).OrderByDescending(VideoScore).ThenByDescending(Bitrate).FirstOrDefault();
            if (picture is not null && PickAudio(audio, picture, preferredLanguage) is { } sound)
            {
                rows.Add(Merged(picture, sound, info.DurationSeconds));
            }
            else if (bucket.Where(f => f.IsMuxed).OrderByDescending(VideoScore).ThenByDescending(Bitrate).FirstOrDefault() is { } both)
            {
                rows.Add(Single(both, info.DurationSeconds));
            }
            else if (picture is not null && audio.Count == 0)
            {
                rows.Add(Single(picture, info.DurationSeconds)); // a silent video
            }
        }

        // No row yet (sites that don't say the resolution, like Instagram's plain MP4 files without FFmpeg): the best
        // format with picture and sound: largest size, then bitrate, then yt-dlp's own order (it lists worst first).
        if (rows.Count == 0 && muxed.Count > 0)
        {
            var best = muxed.Select((f, i) => (Format: f, Index: i))
                .OrderByDescending(x => x.Format.FileSize ?? x.Format.FileSizeApprox ?? 0)
                .ThenByDescending(x => Bitrate(x.Format))
                .ThenByDescending(x => x.Index)
                .First().Format;
            rows.Add(Single(best, info.DurationSeconds));
        }

        var orderedAudio = OrderAudio(audio, null, preferredLanguage).ToList();
        foreach (var family in new[] { "AAC", "Opus" })
        {
            if (orderedAudio.FirstOrDefault(a => AudioFamily(a) == family) is { } best)
            {
                rows.Add(AudioOnly(best, info.DurationSeconds));
            }
        }

        if (rows.Count == 0)
        {
            var separate = !ffmpegAvailable && usable.Any(f => f.IsVideoOnly);
            return new YtDlpChoices([], [], separate ? null : "No downloadable video was found on this page.", separate ? NeedFfmpegOnlyNote : null);
        }

        string? note = null;
        if (!ffmpegAvailable && usable.Any(f => f.IsVideoOnly && f.Height > (rows.Where(r => !r.IsAudioOnly).Max(r => r.Height) ?? 0)))
        {
            note = rows.Any(r => !r.IsAudioOnly) ? NeedFfmpegNote : NeedFfmpegOnlyNote;
        }

        return new YtDlpChoices(rows, ffmpegAvailable ? AudioChoices(orderedAudio) : [], null, note);

        static YtDlpChoices Refuse(string problem) => new([], [], problem, null);
    }

    /// <summary>
    /// The sound to pair with <paramref name="picture"/>: yt-dlp's own preference first (it marks the video's
    /// <b>original</b> track, so an auto-dubbed track doesn't win), then the user's language, then a codec that fits the
    /// picture's container (AAC with H.264/AV1, Opus with VP9), then bitrate.
    /// </summary>
    public static YtDlpFormat? PickAudio(IReadOnlyList<YtDlpFormat> audio, YtDlpFormat? picture, string? preferredLanguage) =>
        OrderAudio(audio, picture, preferredLanguage).FirstOrDefault();

    /// <summary>
    /// The same row with the sound in another language (the user's choice): within that language, the track whose
    /// codec fits the picture (so H.264 keeps AAC and an MP4 file), then the highest bitrate. Rows without separate
    /// sound are returned unchanged.
    /// </summary>
    public static YtDlpQualityRow WithAudio(YtDlpQualityRow row, YtDlpInfo info, YtDlpAudioChoice choice)
    {
        var formats = info.Formats.Where(f => !f.IsJunk && !f.HasDrm).Select(Normalize).ToList();
        if (row.VideoFormatId is null || row.AudioFormatId is null
            || formats.FirstOrDefault(f => f.FormatId == row.VideoFormatId) is not { } picture)
        {
            return row;
        }

        var language = PrimaryLanguage(choice.Language);
        var candidates = formats.Where(f => f.IsAudioOnly && (language is null ? f.FormatId == choice.FormatId : PrimaryLanguage(f.Language) == language)).ToList();
        var pictureContainer = Container(picture, null);
        var sound = candidates
            .OrderByDescending(a => Container(picture, a) == pictureContainer)
            .ThenByDescending(Bitrate)
            .FirstOrDefault();
        return sound is null ? row : Merged(picture, sound, info.DurationSeconds);
    }

    private static IEnumerable<YtDlpFormat> OrderAudio(IEnumerable<YtDlpFormat> audio, YtDlpFormat? picture, string? preferredLanguage)
    {
        var wanted = PrimaryLanguage(preferredLanguage);
        var pictureContainer = picture is null ? null : Container(picture, null);
        return audio
            .OrderByDescending(a => a.LanguagePreference ?? 0)
            .ThenBy(IsDrc)
            .ThenByDescending(a => wanted is not null && PrimaryLanguage(a.Language) == wanted)
            .ThenByDescending(a => pictureContainer is not null && Container(picture!, a) == pictureContainer)
            .ThenByDescending(Bitrate);
    }

    /// <summary>One choice per language (its best track), in the automatic order; empty when there's only one language.</summary>
    private static IReadOnlyList<YtDlpAudioChoice> AudioChoices(IReadOnlyList<YtDlpFormat> orderedAudio)
    {
        var perLanguage = orderedAudio.GroupBy(a => PrimaryLanguage(a.Language) ?? string.Empty).Select(g => g.First()).ToList();
        if (perLanguage.Count < 2)
        {
            return [];
        }

        return perLanguage.Select(a =>
        {
            var name = DashAnalysis.LanguageName(a.Language) ?? "Unknown language";
            var original = (a.FormatNote?.Contains("original", StringComparison.OrdinalIgnoreCase) ?? false) || a.LanguagePreference >= 10 ? " (original)" : string.Empty;
            return new YtDlpAudioChoice($"Sound: {name}{original}", a.FormatId, a.Language);
        }).ToList();
    }

    // ---------------------------------------------------------------- rows

    private static YtDlpQualityRow Merged(YtDlpFormat picture, YtDlpFormat sound, double? duration)
    {
        var container = Container(picture, sound);
        var (bytes, estimate) = Size(picture, duration);
        var (soundBytes, soundEstimate) = Size(sound, duration);
        long? total = bytes is null || soundBytes is null ? null : bytes + soundBytes;
        var extension = container.ToLowerInvariant() switch { "webm" => "webm", "mkv" => "mkv", _ => "mp4" };
        return new YtDlpQualityRow(
            Label(picture, container, total, estimate || soundEstimate),
            $"{picture.FormatId}+{sound.FormatId}",
            extension,
            extension,
            picture.Height,
            picture.Fps,
            total,
            estimate || soundEstimate,
            false,
            picture.FormatId,
            sound.FormatId);
    }

    private static YtDlpQualityRow Single(YtDlpFormat format, double? duration)
    {
        var (bytes, estimate) = Size(format, duration);
        var extension = SafeExtension(format.Extension) ?? "mp4";
        return new YtDlpQualityRow(Label(format, extension.ToUpperInvariant(), bytes, estimate), format.FormatId, null, extension,
            format.Height, format.Fps, bytes, estimate, false, format.FormatId, null);
    }

    private static YtDlpQualityRow AudioOnly(YtDlpFormat format, double? duration)
    {
        var (bytes, estimate) = Size(format, duration);
        var extension = SafeExtension(format.Extension) ?? (AudioFamily(format) == "Opus" ? "webm" : "m4a");
        var parts = new List<string> { "Audio only", AudioFamily(format) ?? format.AudioCodec ?? "Audio", extension.ToUpperInvariant() };
        if ((format.AudioBitrateKbps ?? format.TotalBitrateKbps) is { } kbps)
        {
            parts.Add(HlsAnalysis.Bitrate((long)(kbps * 1000)));
        }

        if (bytes is { } size)
        {
            parts.Add(SizeText(size, estimate));
        }

        return new YtDlpQualityRow(string.Join(" · ", parts), format.FormatId, null, extension, null, null, bytes, estimate, true, null, format.FormatId);
    }

    /// <summary>"1080p · 60fps · HDR · H.264 · MP4 · ≈ 412 MB".</summary>
    private static string Label(YtDlpFormat picture, string container, long? bytes, bool estimate)
    {
        var parts = new List<string>();
        if (picture.Height is { } height)
        {
            parts.Add($"{height}p");
        }
        else if (picture.CodecsUnknown)
        {
            parts.Add("Best available");
        }

        if (picture.Fps is > 31 and var fps)
        {
            parts.Add(string.Format(CultureInfo.InvariantCulture, "{0:0}fps", fps));
        }

        if (picture.DynamicRange is { } range && !range.Equals("SDR", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add("HDR");
        }

        if (HlsAnalysis.FriendlyCodecs(picture.VideoCodec).Video.FirstOrDefault() is { } codec)
        {
            parts.Add(codec);
        }

        parts.Add(container.ToUpperInvariant());
        if (bytes is { } size)
        {
            parts.Add(SizeText(size, estimate));
        }

        return string.Join(" · ", parts);
    }

    private static string SizeText(long bytes, bool estimate) =>
        estimate ? $"≈ {DownloadText.Size(bytes, CultureInfo.CurrentCulture)}" : DownloadText.Size(bytes, CultureInfo.CurrentCulture);

    // ---------------------------------------------------------------- helpers

    /// <summary>Exact size, else yt-dlp's estimate, else bitrate × length (estimates flagged).</summary>
    private static (long? Bytes, bool Estimate) Size(YtDlpFormat format, double? duration)
    {
        if (format.FileSize is { } exact)
        {
            return (exact, false);
        }

        if (format.FileSizeApprox is { } approx)
        {
            return (approx, true);
        }

        var kbps = format.TotalBitrateKbps ?? ((format.VideoBitrateKbps ?? 0) + (format.AudioBitrateKbps ?? 0));
        return kbps > 0 && duration is > 0 ? ((long)(kbps * 1000 * duration.Value / 8), true) : (null, true);
    }

    private static string Container(YtDlpFormat picture, YtDlpFormat? sound) =>
        HlsAnalysis.SelectContainer(string.Join(",", new[] { picture.VideoCodec, sound?.AudioCodec }.Where(c => c is not null))).Container;

    /// <summary>Most playable first: H.264, AV1, VP9, others.</summary>
    private static int VideoScore(YtDlpFormat f) => HlsAnalysis.FriendlyCodecs(f.VideoCodec).Video.FirstOrDefault() switch
    {
        "H.264" => 3,
        "AV1" => 2,
        "VP9" => 1,
        _ => 0,
    };

    /// <summary>YouTube's "stable volume" variants (dynamic range compressed, ids like "251-drc"): only when nothing else.</summary>
    private static bool IsDrc(YtDlpFormat f) =>
        f.FormatId.EndsWith("-drc", StringComparison.OrdinalIgnoreCase) || (f.FormatNote?.Contains("DRC", StringComparison.Ordinal) ?? false);

    private static double Bitrate(YtDlpFormat f) => f.TotalBitrateKbps ?? f.VideoBitrateKbps ?? f.AudioBitrateKbps ?? 0;

    private static int FpsBucket(double? fps) => fps is > 31 ? (int)Math.Round(fps.Value) : 30;

    private static string? AudioFamily(YtDlpFormat f) => HlsAnalysis.FriendlyCodecs(f.AudioCodec).Audio.FirstOrDefault() switch
    {
        "AAC" => "AAC",
        "Opus" => "Opus",
        var other => other,
    };

    /// <summary>Some sites name codecs plainly ("h264", "aac"); map them to the RFC 6381 forms the codec helpers know.</summary>
    private static YtDlpFormat Normalize(YtDlpFormat f) => f with
    {
        VideoCodec = f.VideoCodec?.ToLowerInvariant() switch
        {
            "h264" or "avc" => "avc1",
            "h265" or "hevc" => "hvc1",
            "av1" => "av01",
            _ => f.VideoCodec,
        },
        AudioCodec = f.AudioCodec?.ToLowerInvariant() switch
        {
            "aac" => "mp4a.40.2",
            _ => f.AudioCodec,
        },
    };

    /// <summary>Only plain short extensions from the (untrusted) JSON become part of a file name.</summary>
    private static string? SafeExtension(string? extension) =>
        extension is { Length: > 0 and <= 5 } && extension.All(char.IsAsciiLetterOrDigit) ? extension.ToLowerInvariant() : null;

    private static string? PrimaryLanguage(string? tag) =>
        string.IsNullOrWhiteSpace(tag) ? null : tag.Split('-', '_')[0].Trim().ToLowerInvariant();
}
