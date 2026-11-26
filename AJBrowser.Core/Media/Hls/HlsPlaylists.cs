namespace AJBrowser.Core.Media.Hls;

/// <summary>Either kind of HLS playlist (plan Phase 6). Parse with <see cref="HlsParser.Parse"/>.</summary>
public abstract record HlsPlaylist(Uri BaseUri);

/// <summary>
/// A master (multivariant) playlist: the list of qualities. Each <see cref="Variants"/> entry points at a media
/// playlist; <see cref="Renditions"/> are alternative audio/subtitle tracks (<c>EXT-X-MEDIA</c>).
/// <c>EXT-X-I-FRAME-STREAM-INF</c> (trick-play) streams are ignored.
/// </summary>
public sealed record HlsMasterPlaylist(
    Uri BaseUri,
    IReadOnlyList<HlsVariant> Variants,
    IReadOnlyList<HlsRendition> Renditions,
    IReadOnlyList<HlsKey> SessionKeys) : HlsPlaylist(BaseUri);

/// <summary>One quality: <c>EXT-X-STREAM-INF</c> plus the media playlist address on the next line.</summary>
public sealed record HlsVariant(
    Uri Uri,
    long Bandwidth,
    long? AverageBandwidth,
    int? Width,
    int? Height,
    string? Codecs,
    double? FrameRate,
    string? AudioGroup,
    string? SubtitlesGroup)
{
    /// <summary>The bitrate to estimate size with: the average if given, else the peak.</summary>
    public long EffectiveBandwidth => AverageBandwidth is > 0 ? AverageBandwidth.Value : Bandwidth;
}

/// <summary>An <c>EXT-X-MEDIA</c> track (audio language, subtitles…). <see cref="Uri"/> is null when it's muxed into the video.</summary>
public sealed record HlsRendition(
    string Type,
    string GroupId,
    string? Name,
    string? Language,
    bool IsDefault,
    Uri? Uri);

/// <summary>
/// A media playlist: the pieces (segments) of one quality, in order. <see cref="IsEndList"/> means the whole video is
/// listed (video on demand); without it the stream is live or still growing.
/// </summary>
public sealed record HlsMediaPlaylist(
    Uri BaseUri,
    double TargetDuration,
    long MediaSequence,
    bool IsEndList,
    string? PlaylistType,
    IReadOnlyList<HlsSegment> Segments) : HlsPlaylist(BaseUri)
{
    /// <summary>Sum of the segment durations.</summary>
    public double TotalDurationSeconds => Segments.Sum(s => s.DurationSeconds);

    /// <summary>Not a finished recording: live or still being written (no <c>EXT-X-ENDLIST</c>, not <c>VOD</c>).</summary>
    public bool IsLive => !IsEndList && !string.Equals(PlaylistType, "VOD", StringComparison.OrdinalIgnoreCase);

    /// <summary>fMP4 HLS: segments need an initialisation section (<c>EXT-X-MAP</c>); otherwise MPEG-TS.</summary>
    public bool IsFragmentedMp4 => Segments.Any(s => s.Map is not null);
}

/// <summary>
/// One piece of the video. <see cref="SequenceNumber"/> is its media sequence number (the default AES-128 IV).
/// <see cref="ByteRange"/> is set when the piece is a slice of a bigger file (<c>EXT-X-BYTERANGE</c>).
/// </summary>
public sealed record HlsSegment(
    Uri Uri,
    double DurationSeconds,
    long SequenceNumber,
    HlsByteRange? ByteRange,
    HlsKey? Key,
    HlsMap? Map,
    bool DiscontinuityBefore);

/// <summary>Bytes <see cref="Offset"/> … <see cref="Offset"/> + <see cref="Length"/> − 1 of the resource.</summary>
public sealed record HlsByteRange(long Length, long Offset)
{
    /// <summary>The HTTP Range header value: "bytes=100-199".</summary>
    public string ToRangeHeader() => $"bytes={Offset}-{Offset + Length - 1}";
}

/// <summary>
/// <c>EXT-X-KEY</c> / <c>EXT-X-SESSION-KEY</c>. <see cref="Method"/> is NONE, AES-128 (standard encryption with a
/// key the site serves, supported) or SAMPLE-AES / SAMPLE-AES-CTR (used by DRM, not supported).
/// </summary>
public sealed record HlsKey(string Method, Uri? Uri, byte[]? IV, string? KeyFormat)
{
    public bool IsNone => Method.Equals("NONE", StringComparison.OrdinalIgnoreCase);

    public bool IsAes128 => Method.Equals("AES-128", StringComparison.OrdinalIgnoreCase);

    /// <summary>"identity" (or absent) means a plain key file; anything else is a DRM system (FairPlay, Widevine, PlayReady).</summary>
    public bool IsIdentityKeyFormat => string.IsNullOrEmpty(KeyFormat) || KeyFormat.Equals("identity", StringComparison.OrdinalIgnoreCase);
}

/// <summary><c>EXT-X-MAP</c>: the initialisation section fMP4 segments need in front of them.</summary>
public sealed record HlsMap(Uri Uri, HlsByteRange? ByteRange);
