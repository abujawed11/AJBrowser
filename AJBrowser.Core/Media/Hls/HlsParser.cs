using System.Globalization;

namespace AJBrowser.Core.Media.Hls;

/// <summary>
/// Reads HLS playlists (RFC 8216) into <see cref="HlsMasterPlaylist"/> or <see cref="HlsMediaPlaylist"/>, resolving
/// every address against the playlist's own URL. Lenient about what real servers send (BOMs, CRLF, blank lines,
/// unknown tags, lower-case attribute values), strict about structure: a playlist must start with <c>#EXTM3U</c>.
/// </summary>
public static class HlsParser
{
    /// <summary>Playlists bigger than this aren't playlists (a 10-hour video at 2 s per segment is ~2 MB).</summary>
    public const int MaxLength = 16 * 1024 * 1024;

    /// <summary>Parses a playlist; throws <see cref="FormatException"/> when it isn't one.</summary>
    public static HlsPlaylist Parse(string text, Uri playlistUri)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxLength)
        {
            throw new FormatException("The playlist is too large.");
        }

        var lines = text.TrimStart('﻿').Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0 || !lines[0].StartsWith("#EXTM3U", StringComparison.Ordinal))
        {
            throw new FormatException("Not an HLS playlist (no #EXTM3U).");
        }

        var isMaster = lines.Any(l => l.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal));
        return isMaster ? ParseMaster(lines, playlistUri) : ParseMedia(lines, playlistUri);
    }

    /// <summary>Like <see cref="Parse"/>, but returns null instead of throwing.</summary>
    public static HlsPlaylist? TryParse(string? text, Uri playlistUri)
    {
        if (text is null)
        {
            return null;
        }

        try
        {
            return Parse(text, playlistUri);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static HlsMasterPlaylist ParseMaster(List<string> lines, Uri baseUri)
    {
        var variants = new List<HlsVariant>();
        var renditions = new List<HlsRendition>();
        var sessionKeys = new List<HlsKey>();
        Dictionary<string, string>? pendingStreamInf = null;

        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
            {
                pendingStreamInf = Attributes(line);
            }
            else if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal))
            {
                var a = Attributes(line);
                if (a.TryGetValue("TYPE", out var type) && a.TryGetValue("GROUP-ID", out var group))
                {
                    renditions.Add(new HlsRendition(
                        type.ToUpperInvariant(),
                        group,
                        a.GetValueOrDefault("NAME"),
                        a.GetValueOrDefault("LANGUAGE"),
                        IsYes(a.GetValueOrDefault("DEFAULT")),
                        Resolve(baseUri, a.GetValueOrDefault("URI"))));
                }
            }
            else if (line.StartsWith("#EXT-X-SESSION-KEY:", StringComparison.Ordinal))
            {
                if (Key(line, baseUri) is { } key)
                {
                    sessionKeys.Add(key);
                }
            }
            else if (!line.StartsWith('#'))
            {
                // The address line that belongs to the preceding EXT-X-STREAM-INF.
                if (pendingStreamInf is { } a && Resolve(baseUri, line) is { } uri)
                {
                    var (width, height) = Resolution(a.GetValueOrDefault("RESOLUTION"));
                    variants.Add(new HlsVariant(
                        uri,
                        Long(a.GetValueOrDefault("BANDWIDTH")) ?? 0,
                        Long(a.GetValueOrDefault("AVERAGE-BANDWIDTH")),
                        width,
                        height,
                        a.GetValueOrDefault("CODECS"),
                        Double(a.GetValueOrDefault("FRAME-RATE")),
                        a.GetValueOrDefault("AUDIO"),
                        a.GetValueOrDefault("SUBTITLES")));
                }

                pendingStreamInf = null;
            }
        }

        return new HlsMasterPlaylist(baseUri, variants, renditions, sessionKeys);
    }

    private static HlsMediaPlaylist ParseMedia(List<string> lines, Uri baseUri)
    {
        var segments = new List<HlsSegment>();
        double targetDuration = 0;
        long mediaSequence = 0;
        var endList = false;
        string? playlistType = null;

        double? pendingDuration = null;
        HlsByteRange? pendingRange = null;
        var discontinuity = false;
        HlsKey? currentKey = null;
        HlsMap? currentMap = null;
        var lastRangeEnd = new Dictionary<string, long>(StringComparer.Ordinal); // per resource, for "n" without "@o"
        var sequence = 0L;
        var sequenceStarted = false;

        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal))
            {
                targetDuration = Double(Value(line)) ?? 0;
            }
            else if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
            {
                mediaSequence = Long(Value(line)) ?? 0;
            }
            else if (line.StartsWith("#EXT-X-PLAYLIST-TYPE:", StringComparison.Ordinal))
            {
                playlistType = Value(line).ToUpperInvariant();
            }
            else if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal))
            {
                endList = true;
            }
            else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                pendingDuration = Double(Value(line).Split(',')[0]) ?? 0;
            }
            else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal))
            {
                pendingRange = ByteRangeSpec(Value(line));
            }
            else if (line.StartsWith("#EXT-X-DISCONTINUITY", StringComparison.Ordinal) && !line.StartsWith("#EXT-X-DISCONTINUITY-SEQUENCE", StringComparison.Ordinal))
            {
                discontinuity = true;
            }
            else if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
            {
                var key = Key(line, baseUri);
                currentKey = key is null || key.IsNone ? null : key;
            }
            else if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                var a = Attributes(line);
                if (Resolve(baseUri, a.GetValueOrDefault("URI")) is { } mapUri)
                {
                    var range = a.TryGetValue("BYTERANGE", out var spec) ? ByteRangeSpec(spec) : null;
                    currentMap = new HlsMap(mapUri, range is { Offset: < 0 } ? range with { Offset = 0 } : range);
                }
            }
            else if (!line.StartsWith('#'))
            {
                if (pendingDuration is null)
                {
                    continue; // an address without #EXTINF isn't a segment
                }

                if (Resolve(baseUri, line) is not { } uri)
                {
                    // The line after #EXTINF is that segment's address; if it's unusable the segment is dropped and its
                    // tags don't carry over to a later line.
                    pendingDuration = null;
                    pendingRange = null;
                    continue;
                }

                if (!sequenceStarted)
                {
                    sequence = mediaSequence;
                    sequenceStarted = true;
                }

                HlsByteRange? range = null;
                if (pendingRange is { } spec)
                {
                    var resource = uri.AbsoluteUri;
                    var offset = spec.Offset >= 0 ? spec.Offset : lastRangeEnd.GetValueOrDefault(resource);
                    range = new HlsByteRange(spec.Length, offset);
                    lastRangeEnd[resource] = offset + spec.Length;
                }

                segments.Add(new HlsSegment(uri, pendingDuration.Value, sequence++, range, currentKey, currentMap, discontinuity));
                pendingDuration = null;
                pendingRange = null;
                discontinuity = false;
            }
        }

        return new HlsMediaPlaylist(baseUri, targetDuration, mediaSequence, endList, playlistType, segments);
    }

    // ---------------------------------------------------------------- tag pieces

    /// <summary>The part after "#TAG:".</summary>
    private static string Value(string line)
    {
        var colon = line.IndexOf(':');
        return colon >= 0 ? line[(colon + 1)..].Trim() : string.Empty;
    }

    /// <summary>
    /// An attribute list: <c>NAME=value,NAME="quoted, with commas"</c>. Names are upper-cased; quotes are removed.
    /// </summary>
    internal static Dictionary<string, string> Attributes(string line)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var text = Value(line);
        var i = 0;
        while (i < text.Length)
        {
            var eq = text.IndexOf('=', i);
            if (eq < 0)
            {
                break;
            }

            var name = text[i..eq].Trim().TrimStart(',').Trim().ToUpperInvariant();
            i = eq + 1;
            string value;
            if (i < text.Length && text[i] == '"')
            {
                var close = text.IndexOf('"', i + 1);
                if (close < 0)
                {
                    close = text.Length;
                }

                value = text[(i + 1)..close];
                i = Math.Min(text.Length, close + 1);
                var comma = text.IndexOf(',', i);
                i = comma < 0 ? text.Length : comma + 1;
            }
            else
            {
                var comma = text.IndexOf(',', i);
                value = (comma < 0 ? text[i..] : text[i..comma]).Trim();
                i = comma < 0 ? text.Length : comma + 1;
            }

            if (name.Length > 0)
            {
                result[name] = value;
            }
        }

        return result;
    }

    private static HlsKey? Key(string line, Uri baseUri)
    {
        var a = Attributes(line);
        if (!a.TryGetValue("METHOD", out var method))
        {
            return null;
        }

        return new HlsKey(method.ToUpperInvariant(), Resolve(baseUri, a.GetValueOrDefault("URI")), Hex(a.GetValueOrDefault("IV")), a.GetValueOrDefault("KEYFORMAT"));
    }

    /// <summary>"length[@offset]"; Offset is -1 when absent (continues after the previous range).</summary>
    private static HlsByteRange? ByteRangeSpec(string spec)
    {
        var parts = spec.Split('@');
        if (Long(parts[0]) is not { } length || length <= 0)
        {
            return null;
        }

        return new HlsByteRange(length, parts.Length > 1 && Long(parts[1]) is { } offset ? offset : -1);
    }

    private static (int? Width, int? Height) Resolution(string? value)
    {
        var parts = value?.ToLowerInvariant().Split('x');
        return parts is { Length: 2 } && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var w)
               && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var h) && w > 0 && h > 0
            ? (w, h)
            : (null, null);
    }

    /// <summary>"0x1A2B…" → 16 bytes; anything else → null.</summary>
    private static byte[]? Hex(string? value)
    {
        if (value is null || !value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hex = value[2..].PadLeft(32, '0');
        if (hex.Length != 32)
        {
            return null;
        }

        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static Uri? Resolve(Uri baseUri, string? reference) =>
        !string.IsNullOrWhiteSpace(reference) && Uri.TryCreate(baseUri, reference.Trim(), out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri
            : null;

    private static bool IsYes(string? value) => string.Equals(value, "YES", StringComparison.OrdinalIgnoreCase);

    private static long? Long(string? value) =>
        long.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static double? Double(string? value) =>
        double.TryParse(value?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
}
