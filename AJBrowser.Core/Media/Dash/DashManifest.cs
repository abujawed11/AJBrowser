using AJBrowser.Core.Media.Hls;

namespace AJBrowser.Core.Media.Dash;

/// <summary>
/// A DASH manifest (<c>.mpd</c>, plan Phase 7), reduced to what AJBrowser needs: the tracks of one period, each with
/// the full list of pieces to fetch. Parse with <see cref="DashParser.Parse"/>.
/// </summary>
/// <param name="BaseUri">The manifest's own address (every piece address is already resolved against it).</param>
/// <param name="IsDynamic"><c>MPD@type="dynamic"</c>: a live stream.</param>
/// <param name="DurationSeconds">The length of the video, when the manifest says.</param>
/// <param name="PeriodCount">How many <c>Period</c>s the manifest has; only the first is read (<see cref="Representations"/>).</param>
/// <param name="Representations">Every track (quality or audio language) of the first period, in manifest order.</param>
public sealed record DashManifest(
    Uri BaseUri,
    bool IsDynamic,
    double? DurationSeconds,
    int PeriodCount,
    IReadOnlyList<DashRepresentation> Representations)
{
    /// <summary>Picture tracks (with or without sound inside).</summary>
    public IReadOnlyList<DashRepresentation> Video => Representations.Where(r => r.Kind is DashTrackKind.Video or DashTrackKind.Muxed).ToList();

    /// <summary>Sound-only tracks.</summary>
    public IReadOnlyList<DashRepresentation> Audio => Representations.Where(r => r.Kind == DashTrackKind.Audio).ToList();

    /// <summary>Any track carries <c>ContentProtection</c> (DRM).</summary>
    public bool IsProtected => Representations.Any(r => r.IsProtected);
}

/// <summary>What a track holds.</summary>
public enum DashTrackKind
{
    /// <summary>Picture only (the usual case in DASH: sound is a separate track).</summary>
    Video,

    /// <summary>Sound only.</summary>
    Audio,

    /// <summary>Picture with the sound already inside.</summary>
    Muxed,

    /// <summary>Subtitles or captions (not downloaded yet, plan §9).</summary>
    Text,

    /// <summary>Something else (thumbnails, unknown types).</summary>
    Other,
}

/// <summary>
/// One <c>Representation</c>: a quality of the picture or one audio track, with its pieces.
/// Attributes inherited from the <c>AdaptationSet</c> are already merged in.
/// </summary>
public sealed record DashRepresentation(
    string? Id,
    DashTrackKind Kind,
    string? MimeType,
    string? Codecs,
    long Bandwidth,
    int? Width,
    int? Height,
    double? FrameRate,
    string? Language,
    string? Label,
    int? AudioChannels,
    bool IsMainRole,
    bool IsProtected,
    DashSegment? Initialization,
    IReadOnlyList<DashSegment> Segments)
{
    /// <summary>Sum of the piece durations (0 when the track is one whole file).</summary>
    public double SegmentDurationSeconds => Segments.Sum(s => s.DurationSeconds);

    /// <summary>The track is a single file fetched whole (<c>SegmentBase</c> or a bare <c>BaseURL</c>).</summary>
    public bool IsSingleFile => Initialization is null && Segments.Count == 1 && Segments[0].ByteRange is null;
}

/// <summary>
/// One piece to fetch: an address, optionally a byte range of it (<c>mediaRange</c>, <c>Initialization@range</c>), and
/// its duration when known. The byte range type is shared with HLS so both feed the same piece downloader.
/// </summary>
public sealed record DashSegment(Uri Uri, HlsByteRange? ByteRange, double DurationSeconds);
