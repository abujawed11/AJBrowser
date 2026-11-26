using System.Globalization;
using System.Security.Cryptography;
using AJBrowser.Core.Downloads;

namespace AJBrowser.Core.Media.Hls;

/// <summary>Whether AJBrowser can download a stream (plan Phase 6).</summary>
public enum HlsSupport
{
    /// <summary>Plain or AES-128 encrypted (standard HLS with a key the site serves, not DRM): downloadable.</summary>
    Supported,

    /// <summary>DRM (SAMPLE-AES, FairPlay, Widevine, PlayReady): never supported.</summary>
    Protected,

    /// <summary>A live stream (no end): not supported yet.</summary>
    Live,

    /// <summary>A playlist with nothing in it.</summary>
    Empty,
}

/// <summary>Decisions about a stream, from its playlists. Pure functions, unit-tested.</summary>
public static class HlsAnalysis
{
    /// <summary>
    /// Can this media playlist be downloaded? DRM wins over live. AES-128 with a plain ("identity") key is supported;
    /// SAMPLE-AES or any DRM key format is not.
    /// </summary>
    public static HlsSupport Classify(HlsMediaPlaylist playlist)
    {
        if (playlist.Segments.Any(s => s.Key is { } key && IsDrm(key)))
        {
            return HlsSupport.Protected;
        }

        if (playlist.IsLive)
        {
            return HlsSupport.Live;
        }

        return playlist.Segments.Count == 0 ? HlsSupport.Empty : HlsSupport.Supported;
    }

    /// <summary>A master playlist announcing DRM keys up front (<c>EXT-X-SESSION-KEY</c>) is protected.</summary>
    public static bool IsProtected(HlsMasterPlaylist master) => master.SessionKeys.Any(IsDrm);

    /// <summary>The user-facing sentence for a support state (null when supported).</summary>
    public static string? Explain(HlsSupport support) => support switch
    {
        HlsSupport.Protected => "Protected (DRM): can't be downloaded",
        HlsSupport.Live => "Live stream: not supported yet",
        HlsSupport.Empty => "The stream is empty",
        _ => null,
    };

    private static bool IsDrm(HlsKey key) => !key.IsNone && (!key.IsAes128 || !key.IsIdentityKeyFormat);

    // ---------------------------------------------------------------- codecs

    /// <summary>
    /// "avc1.64001f,mp4a.40.2" → (["H.264"], ["AAC"]). Unknown codec strings are kept as they are, so nothing is
    /// silently dropped. Video and audio are told apart by their codec family.
    /// </summary>
    public static (IReadOnlyList<string> Video, IReadOnlyList<string> Audio) FriendlyCodecs(string? codecs)
    {
        var video = new List<string>();
        var audio = new List<string>();
        foreach (var raw in (codecs ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var codec = raw.ToLowerInvariant();
            var (name, isVideo) = codec switch
            {
                _ when codec.StartsWith("avc1", StringComparison.Ordinal) || codec.StartsWith("avc3", StringComparison.Ordinal) => ("H.264", true),
                _ when codec.StartsWith("hvc1", StringComparison.Ordinal) || codec.StartsWith("hev1", StringComparison.Ordinal) => ("H.265", true),
                _ when codec.StartsWith("av01", StringComparison.Ordinal) => ("AV1", true),
                _ when codec.StartsWith("vp09", StringComparison.Ordinal) || codec == "vp9" => ("VP9", true),
                _ when codec.StartsWith("vp08", StringComparison.Ordinal) || codec == "vp8" => ("VP8", true),
                _ when codec.StartsWith("mp4a.40", StringComparison.Ordinal) => ("AAC", false),
                "mp4a.69" or "mp4a.6b" or "mp3" => ("MP3", false),
                "ac-3" => ("AC-3", false),
                "ec-3" => ("E-AC-3", false),
                "opus" => ("Opus", false),
                "vorbis" => ("Vorbis", false),
                "flac" => ("FLAC", false),
                _ => (raw, !codec.StartsWith("mp4a", StringComparison.Ordinal)),
            };

            var list = isVideo ? video : audio;
            if (!list.Contains(name))
            {
                list.Add(name);
            }
        }

        return (video, audio);
    }

    /// <summary>"H.264/AAC", "H.265", "AAC", or null when the playlist doesn't say.</summary>
    public static string? CodecLabel(string? codecs)
    {
        var (video, audio) = FriendlyCodecs(codecs);
        var all = video.Concat(audio).ToList();
        return all.Count == 0 ? null : string.Join("/", all);
    }

    /// <summary>
    /// The output container for these codecs (plan §6.4): H.264/H.265/AV1 with AAC/MP3/AC-3/E-AC-3 → MP4; VP8/VP9/AV1 with
    /// Opus/Vorbis → WebM; anything else → MKV, which takes nearly everything. Unknown codecs → MP4 (HLS without a CODECS
    /// attribute is almost always H.264/AAC), with a reason saying so.
    /// </summary>
    public static (string Container, string Reason) SelectContainer(string? codecs)
    {
        var (video, audio) = FriendlyCodecs(codecs);
        if (video.Count == 0 && audio.Count == 0)
        {
            return ("MP4", "The stream doesn't list its codecs; HLS is almost always H.264/AAC.");
        }

        string[] mp4Video = ["H.264", "H.265", "AV1"];
        string[] mp4Audio = ["AAC", "MP3", "AC-3", "E-AC-3"];
        string[] webmVideo = ["VP8", "VP9", "AV1"];
        string[] webmAudio = ["Opus", "Vorbis"];

        if (video.All(mp4Video.Contains) && audio.All(mp4Audio.Contains))
        {
            return ("MP4", "H.264/H.265/AV1 with AAC-family audio fit in MP4.");
        }

        if (video.All(webmVideo.Contains) && audio.All(webmAudio.Contains))
        {
            return ("WebM", "VP8/VP9/AV1 with Opus/Vorbis fit in WebM.");
        }

        return ("MKV", $"{string.Join("/", video.Concat(audio))} don't all fit in MP4 or WebM; MKV keeps them without converting.");
    }

    // ---------------------------------------------------------------- labels

    /// <summary>Estimated size: bitrate × duration ÷ 8. Always shown as "≈ … est." (plan §6.2 step 6).</summary>
    public static long? EstimateBytes(long bitsPerSecond, double durationSeconds) =>
        bitsPerSecond > 0 && durationSeconds > 0 ? (long)(bitsPerSecond * durationSeconds / 8) : null;

    /// <summary>"5.2 Mbps", "800 kbps".</summary>
    public static string Bitrate(long bitsPerSecond) =>
        bitsPerSecond >= 1_000_000
            ? string.Format(CultureInfo.InvariantCulture, "{0:0.#} Mbps", bitsPerSecond / 1_000_000.0)
            : string.Format(CultureInfo.InvariantCulture, "{0} kbps", Math.Max(1, bitsPerSecond / 1000));

    /// <summary>
    /// "1080p · 5.2 Mbps · H.264/AAC · ≈ 740 MB est." Resolution only if the playlist gives it (else just the bitrate);
    /// the size part only when the duration is known (after fetching that quality's media playlist).
    /// </summary>
    public static string VariantLabel(HlsVariant variant, double? durationSeconds, IFormatProvider? culture = null)
    {
        var parts = new List<string>();
        if (variant.Height is > 0)
        {
            parts.Add($"{variant.Height}p");
        }

        if (variant.Bandwidth > 0)
        {
            parts.Add(Bitrate(variant.EffectiveBandwidth));
        }

        if (CodecLabel(variant.Codecs) is { } codecs)
        {
            parts.Add(codecs);
        }

        if (durationSeconds is { } seconds && EstimateBytes(variant.EffectiveBandwidth, seconds) is { } bytes)
        {
            parts.Add($"≈ {DownloadText.Size(bytes, culture)} est.");
        }

        return parts.Count == 0 ? "Unknown quality" : string.Join(" · ", parts);
    }

    /// <summary>Best quality first: height, then bitrate.</summary>
    public static IReadOnlyList<HlsVariant> SortBestFirst(IEnumerable<HlsVariant> variants) =>
        variants.OrderByDescending(v => v.Height ?? 0).ThenByDescending(v => v.EffectiveBandwidth).ToList();
}

/// <summary>
/// AES-128 decryption of HLS segments (RFC 8216 §5.2): AES-128-CBC with PKCS#7 padding, 16-byte key from the key
/// address. The IV is the <c>IV</c> attribute, or else the segment's media sequence number as a 16-byte big-endian
/// integer. This is standard HLS encryption with a key the site hands to every viewer, not DRM.
/// </summary>
public static class HlsDecryptor
{
    public const int KeyLength = 16;

    /// <summary>The IV for a segment: the key's own IV, or its sequence number (big-endian, left-padded with zeros).</summary>
    public static byte[] IvFor(HlsSegment segment)
    {
        if (segment.Key?.IV is { Length: 16 } iv)
        {
            return iv;
        }

        var result = new byte[16];
        var sequence = segment.SequenceNumber;
        for (var i = 15; i >= 8; i--)
        {
            result[i] = (byte)(sequence & 0xFF);
            sequence >>= 8;
        }

        return result;
    }

    /// <summary>Decrypts one segment. Throws <see cref="CryptographicException"/> for a wrong key or damaged data.</summary>
    public static byte[] Decrypt(byte[] encrypted, byte[] key, byte[] iv)
    {
        if (key.Length != KeyLength)
        {
            throw new CryptographicException($"An HLS key must be {KeyLength} bytes (got {key.Length}).");
        }

        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptCbc(encrypted, iv, PaddingMode.PKCS7);
    }
}
