using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using AJBrowser.Core.Media.Hls;

namespace AJBrowser.Core.Media.Dash;

/// <summary>
/// Reads DASH manifests (ISO/IEC 23009-1 <c>.mpd</c>) into a <see cref="DashManifest"/> with every piece address
/// resolved. Supports the four ways DASH lists pieces: <c>SegmentTemplate</c> with <c>$Number$</c> (fixed duration) or
/// <c>SegmentTimeline</c> (<c>$Time$</c> or <c>$Number$</c>), <c>SegmentList</c>, and <c>SegmentBase</c> / a bare
/// <c>BaseURL</c> (one whole file). Segment settings are inherited Period → AdaptationSet → Representation, and
/// <c>BaseURL</c>s are resolved at every level. Only the first <c>Period</c> is read (multi-period is refused in
/// <see cref="DashAnalysis.Classify"/>). XML DTDs are refused (no entity expansion from page-controlled input).
/// </summary>
public static class DashParser
{
    /// <summary>Manifests bigger than this aren't manifests.</summary>
    public const int MaxLength = 16 * 1024 * 1024;

    /// <summary>A track with more pieces than this is skipped (a 24-hour video at 1 s pieces is 86,400).</summary>
    public const int MaxSegmentsPerTrack = 200_000;

    /// <summary>Parses a manifest; throws <see cref="FormatException"/> when it isn't one.</summary>
    public static DashManifest Parse(string text, Uri manifestUri)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(manifestUri);
        if (text.Length > MaxLength)
        {
            throw new FormatException("The manifest is too large.");
        }

        var root = Load(text).Root;
        if (root is null || root.Name.LocalName != "MPD")
        {
            throw new FormatException("Not a DASH manifest (no MPD element).");
        }

        var periods = Children(root, "Period").ToList();
        if (periods.Count == 0)
        {
            throw new FormatException("The DASH manifest has no Period.");
        }

        var isDynamic = string.Equals(Attr(root, "type"), "dynamic", StringComparison.OrdinalIgnoreCase);
        var mpdDuration = ParseDuration(Attr(root, "mediaPresentationDuration"));
        var period = periods[0];
        var periodDuration = PeriodDuration(periods, mpdDuration);

        var mpdBase = WithBaseUrl(manifestUri, root);
        var periodBase = WithBaseUrl(mpdBase, period);
        var representations = new List<DashRepresentation>();
        foreach (var set in Children(period, "AdaptationSet"))
        {
            var setBase = WithBaseUrl(periodBase, set);
            foreach (var rep in Children(set, "Representation"))
            {
                if (ReadRepresentation(manifestUri, period, set, rep, WithBaseUrl(setBase, rep), periodDuration) is { } track)
                {
                    representations.Add(track);
                }
            }
        }

        return new DashManifest(manifestUri, isDynamic, mpdDuration ?? periodDuration, periods.Count, representations);
    }

    /// <summary>Like <see cref="Parse"/>, but returns null instead of throwing.</summary>
    public static DashManifest? TryParse(string? text, Uri manifestUri)
    {
        if (text is null)
        {
            return null;
        }

        try
        {
            return Parse(text, manifestUri);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- ISO 8601 durations

    private static readonly Regex DurationPattern = new(
        @"^P(?:(?<d>\d+(?:\.\d+)?)D)?(?:T(?:(?<h>\d+(?:\.\d+)?)H)?(?:(?<m>\d+(?:\.\d+)?)M)?(?:(?<s>\d+(?:\.\d+)?)S)?)?$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// "PT1H2M3.5S" → 3723.5 seconds; "P1DT2H" works too. Years and months (no fixed length) and malformed values → null.
    /// </summary>
    public static double? ParseDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = DurationPattern.Match(value.Trim());
        if (!match.Success || value.Trim() is "P" or "PT" || value.Trim().EndsWith('T'))
        {
            return null;
        }

        double Part(string name) =>
            match.Groups[name].Success ? double.Parse(match.Groups[name].Value, NumberStyles.Float, CultureInfo.InvariantCulture) : 0;

        return (Part("d") * 86_400) + (Part("h") * 3_600) + (Part("m") * 60) + Part("s");
    }

    // ---------------------------------------------------------------- templates

    /// <summary>
    /// Fills a <c>SegmentTemplate</c> address: <c>$RepresentationID$</c>, <c>$Number$</c>, <c>$Time$</c>,
    /// <c>$Bandwidth$</c>, optional width tags (<c>$Number%05d$</c>) and <c>$$</c> for a literal dollar.
    /// Returns null for an unknown identifier, an unclosed <c>$</c>, or a missing representation id.
    /// </summary>
    public static string? ExpandTemplate(string template, string? representationId, long number, long time, long bandwidth)
    {
        ArgumentNullException.ThrowIfNull(template);
        var result = new StringBuilder(template.Length + 16);
        var i = 0;
        while (i < template.Length)
        {
            var start = template.IndexOf('$', i);
            if (start < 0)
            {
                result.Append(template, i, template.Length - i);
                break;
            }

            var end = template.IndexOf('$', start + 1);
            if (end < 0)
            {
                return null;
            }

            result.Append(template, i, start - i);
            var identifier = template[(start + 1)..end];
            i = end + 1;
            if (identifier.Length == 0)
            {
                result.Append('$');
                continue;
            }

            var percent = identifier.IndexOf('%');
            var name = percent < 0 ? identifier : identifier[..percent];
            var format = percent < 0 ? null : identifier[percent..];
            if (name == "RepresentationID")
            {
                if (representationId is null || format is not null)
                {
                    return null;
                }

                result.Append(representationId);
                continue;
            }

            long? value = name switch
            {
                "Number" => number,
                "Time" => time,
                "Bandwidth" => bandwidth,
                _ => null,
            };
            if (value is null || FormatNumber(value.Value, format) is not { } text)
            {
                return null;
            }

            result.Append(text);
        }

        return result.ToString();
    }

    private static string? FormatNumber(long value, string? format)
    {
        var digits = value.ToString(CultureInfo.InvariantCulture);
        if (format is null)
        {
            return digits;
        }

        // "%05d", "%5d" (DASH always pads with zeros) or "%d".
        if (format.Length < 2 || format[0] != '%' || format[^1] != 'd')
        {
            return null;
        }

        var widthText = format[1..^1];
        if (widthText.Length == 0)
        {
            return digits;
        }

        return int.TryParse(widthText, NumberStyles.None, CultureInfo.InvariantCulture, out var width) && width is > 0 and <= 32
            ? digits.PadLeft(width, '0')
            : null;
    }

    // ---------------------------------------------------------------- representations

    private static DashRepresentation? ReadRepresentation(
        Uri manifestUri, XElement period, XElement set, XElement rep, Uri repBase, double? periodDuration)
    {
        var id = Attr(rep, "id");
        var mimeType = Attr(rep, "mimeType") ?? Attr(set, "mimeType");
        var codecs = Attr(rep, "codecs") ?? Attr(set, "codecs");
        var bandwidth = ParseLong(Attr(rep, "bandwidth")) ?? 0;
        var kind = KindOf(Attr(rep, "contentType") ?? Attr(set, "contentType"), mimeType, codecs);
        var label = Text(Children(rep, "Label").FirstOrDefault()) ?? Text(Children(set, "Label").FirstOrDefault()) ?? Attr(set, "label");
        var channels = Children(rep, "AudioChannelConfiguration").Concat(Children(set, "AudioChannelConfiguration"))
            .Select(e => ParseInt(Attr(e, "value")))
            .FirstOrDefault(v => v is > 0 and <= 64);
        var isMain = Children(set, "Role").Concat(Children(rep, "Role"))
            .Any(r => string.Equals(Attr(r, "value"), "main", StringComparison.OrdinalIgnoreCase));
        var isProtected = Children(set, "ContentProtection").Any() || Children(rep, "ContentProtection").Any();

        var segments = ReadSegments(manifestUri, [period, set, rep], repBase, id, bandwidth, periodDuration);
        if (segments is null)
        {
            return null;
        }

        return new DashRepresentation(
            id,
            kind,
            mimeType,
            codecs,
            bandwidth,
            ParseInt(Attr(rep, "width")) ?? ParseInt(Attr(set, "width")),
            ParseInt(Attr(rep, "height")) ?? ParseInt(Attr(set, "height")),
            ParseFrameRate(Attr(rep, "frameRate") ?? Attr(set, "frameRate")),
            Attr(set, "lang") ?? Attr(rep, "lang"),
            label,
            channels,
            isMain,
            isProtected,
            segments.Value.Init,
            segments.Value.Media);
    }

    private static DashTrackKind KindOf(string? contentType, string? mimeType, string? codecs)
    {
        var lowerCodecs = (codecs ?? string.Empty).ToLowerInvariant();
        var lowerMime = (mimeType ?? string.Empty).ToLowerInvariant();
        var type = (contentType ?? lowerMime.Split('/')[0]).ToLowerInvariant();

        if (type == "text" || lowerMime is "application/ttml+xml" or "text/vtt"
            || lowerCodecs.StartsWith("stpp", StringComparison.Ordinal) || lowerCodecs.StartsWith("wvtt", StringComparison.Ordinal))
        {
            return DashTrackKind.Text;
        }

        if (type == "image")
        {
            return DashTrackKind.Other;
        }

        var (video, audio) = HlsAnalysis.FriendlyCodecs(codecs);
        return type switch
        {
            "video" => audio.Count > 0 ? DashTrackKind.Muxed : DashTrackKind.Video,
            "audio" => DashTrackKind.Audio,
            _ when video.Count > 0 && audio.Count > 0 => DashTrackKind.Muxed,
            _ when video.Count > 0 => DashTrackKind.Video,
            _ when audio.Count > 0 => DashTrackKind.Audio,
            _ => DashTrackKind.Other,
        };
    }

    private readonly record struct SegmentInfo(DashSegment? Init, IReadOnlyList<DashSegment> Media);

    /// <summary>
    /// The pieces of one representation. The deepest level that has a <c>SegmentTemplate</c>, <c>SegmentList</c> or
    /// <c>SegmentBase</c> decides the kind; that kind's attributes are then merged from all levels (inner wins).
    /// Returns null when the track is unusable (bad template, bad range, too many pieces).
    /// </summary>
    private static SegmentInfo? ReadSegments(
        Uri manifestUri, XElement[] levels, Uri baseUri, string? id, long bandwidth, double? periodDuration)
    {
        string[] kinds = ["SegmentTemplate", "SegmentList", "SegmentBase"];
        var kind = levels.Reverse()
            .Select(level => kinds.FirstOrDefault(k => Children(level, k).Any()))
            .FirstOrDefault(k => k is not null);

        var elements = levels.Select(level => kind is null ? null : Children(level, kind).FirstOrDefault())
            .OfType<XElement>()
            .ToList();

        return kind switch
        {
            "SegmentTemplate" => FromTemplate(elements, baseUri, id, bandwidth, periodDuration),
            "SegmentList" => FromList(elements, baseUri),
            _ => WholeFile(manifestUri, baseUri, periodDuration),
        };
    }

    /// <summary><c>SegmentBase</c> or nothing: the representation is one file at its <c>BaseURL</c>, fetched whole.</summary>
    private static SegmentInfo? WholeFile(Uri manifestUri, Uri baseUri, double? periodDuration)
    {
        // Without any BaseURL the "file" would be the manifest itself.
        if (baseUri == manifestUri || !IsHttp(baseUri))
        {
            return null;
        }

        return new SegmentInfo(null, [new DashSegment(baseUri, null, periodDuration ?? 0)]);
    }

    private static SegmentInfo? FromTemplate(List<XElement> elements, Uri baseUri, string? id, long bandwidth, double? periodDuration)
    {
        string? Merged(string name) => elements.Select(e => Attr(e, name)).LastOrDefault(v => v is not null);

        var media = Merged("media");
        var timescale = Positive(ParseLong(Merged("timescale"))) ?? 1;
        var startNumber = ParseLong(Merged("startNumber")) ?? 1;
        var fixedDuration = ParseLong(Merged("duration"));
        var timeline = elements.Select(e => Children(e, "SegmentTimeline").FirstOrDefault()).LastOrDefault(t => t is not null);

        DashSegment? init = null;
        var initElement = elements.Select(e => Children(e, "Initialization").FirstOrDefault()).LastOrDefault(i => i is not null);
        var initTemplate = Merged("initialization") ?? (initElement is null ? null : Attr(initElement, "sourceURL"));
        if (initTemplate is not null)
        {
            if (ExpandTemplate(initTemplate, id, startNumber, 0, bandwidth) is not { } initText || Resolve(baseUri, initText) is not { } initUri)
            {
                return null;
            }

            init = new DashSegment(initUri, null, 0);
        }

        if (media is null)
        {
            return null;
        }

        var pieces = new List<DashSegment>();
        bool Add(long number, long time, long duration)
        {
            if (pieces.Count >= MaxSegmentsPerTrack
                || ExpandTemplate(media, id, number, time, bandwidth) is not { } text
                || Resolve(baseUri, text) is not { } uri)
            {
                return false;
            }

            pieces.Add(new DashSegment(uri, null, duration / (double)timescale));
            return true;
        }

        if (timeline is not null)
        {
            var entries = Children(timeline, "S").ToList();
            var number = startNumber;
            long time = 0;
            for (var i = 0; i < entries.Count; i++)
            {
                var s = entries[i];
                if (ParseLong(Attr(s, "d")) is not (> 0 and var d))
                {
                    return null;
                }

                time = ParseLong(Attr(s, "t")) ?? time;
                var repeat = ParseLong(Attr(s, "r")) ?? 0;
                long count;
                if (repeat >= 0)
                {
                    count = repeat + 1;
                }
                else
                {
                    // r = -1: repeat until the next entry's start, or the end of the period.
                    var nextStart = i + 1 < entries.Count ? ParseLong(Attr(entries[i + 1], "t")) : null;
                    var end = nextStart ?? (periodDuration is { } p ? (long)Math.Round(p * timescale) : (long?)null);
                    count = end is { } e && e > time ? (long)Math.Ceiling((e - time) / (double)d) : 1;
                }

                if (count > MaxSegmentsPerTrack)
                {
                    return null;
                }

                for (var k = 0; k < count; k++)
                {
                    if (!Add(number, time, d))
                    {
                        return null;
                    }

                    number++;
                    time += d;
                }
            }
        }
        else if (fixedDuration is > 0 && periodDuration is > 0)
        {
            var total = (long)Math.Round(periodDuration.Value * timescale);
            var count = (long)Math.Ceiling(total / (double)fixedDuration.Value - 1e-9);
            if (count > MaxSegmentsPerTrack)
            {
                return null;
            }

            for (long k = 0; k < count; k++)
            {
                var time = k * fixedDuration.Value;
                if (!Add(startNumber + k, time, Math.Min(fixedDuration.Value, total - time)))
                {
                    return null;
                }
            }
        }
        else
        {
            return null;
        }

        return new SegmentInfo(init, pieces);
    }

    private static SegmentInfo? FromList(List<XElement> elements, Uri baseUri)
    {
        string? Merged(string name) => elements.Select(e => Attr(e, name)).LastOrDefault(v => v is not null);

        var timescale = Positive(ParseLong(Merged("timescale"))) ?? 1;
        var duration = Positive(ParseLong(Merged("duration"))) is { } d ? d / (double)timescale : 0;

        DashSegment? init = null;
        if (elements.Select(e => Children(e, "Initialization").FirstOrDefault()).LastOrDefault(i => i is not null) is { } initElement)
        {
            if (Piece(baseUri, Attr(initElement, "sourceURL"), Attr(initElement, "range"), 0) is not { } piece)
            {
                return null;
            }

            init = piece;
        }

        var urls = elements.Select(e => Children(e, "SegmentURL").ToList()).LastOrDefault(list => list.Count > 0) ?? [];
        if (urls.Count is 0 or > MaxSegmentsPerTrack)
        {
            return null;
        }

        var pieces = new List<DashSegment>(urls.Count);
        foreach (var url in urls)
        {
            if (Piece(baseUri, Attr(url, "media"), Attr(url, "mediaRange"), duration) is not { } piece)
            {
                return null;
            }

            pieces.Add(piece);
        }

        return new SegmentInfo(init, pieces);
    }

    /// <summary>A piece from an address (absent = the base file itself) and an optional "first-last" byte range.</summary>
    private static DashSegment? Piece(Uri baseUri, string? address, string? range, double duration)
    {
        var uri = string.IsNullOrWhiteSpace(address) ? (IsHttp(baseUri) ? baseUri : null) : Resolve(baseUri, address);
        if (uri is null)
        {
            return null;
        }

        HlsByteRange? byteRange = null;
        if (range is not null)
        {
            byteRange = ParseRange(range);
            if (byteRange is null)
            {
                return null;
            }
        }

        return new DashSegment(uri, byteRange, duration);
    }

    /// <summary>"100-199" → 100 bytes from offset 100.</summary>
    internal static HlsByteRange? ParseRange(string value)
    {
        var parts = value.Trim().Split('-');
        return parts.Length == 2
               && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first)
               && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var last)
               && last >= first
            ? new HlsByteRange(last - first + 1, first)
            : null;
    }

    // ---------------------------------------------------------------- helpers

    private static XDocument Load(string text)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersInDocument = MaxLength,
        };

        try
        {
            using var reader = XmlReader.Create(new StringReader(text.TrimStart('﻿')), settings);
            return XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new FormatException("The DASH manifest isn't valid XML.", ex);
        }
    }

    /// <summary>The period's length: its own <c>duration</c>, else up to the next period's start, else the rest of the MPD.</summary>
    private static double? PeriodDuration(List<XElement> periods, double? mpdDuration)
    {
        var first = periods[0];
        if (ParseDuration(Attr(first, "duration")) is { } own)
        {
            return own;
        }

        var start = ParseDuration(Attr(first, "start")) ?? 0;
        if (periods.Count > 1 && ParseDuration(Attr(periods[1], "start")) is { } nextStart && nextStart > start)
        {
            return nextStart - start;
        }

        return mpdDuration is { } total && total > start ? total - start : null;
    }

    /// <summary>The element's first <c>BaseURL</c> resolved against the parent base (other CDNs in the list are ignored).</summary>
    private static Uri WithBaseUrl(Uri parent, XElement element)
    {
        var text = Text(Children(element, "BaseURL").FirstOrDefault());
        return text is not null && Uri.TryCreate(parent, text, out var uri) ? uri : parent;
    }

    private static Uri? Resolve(Uri baseUri, string reference) =>
        Uri.TryCreate(baseUri, reference.Trim(), out var uri) && IsHttp(uri) ? uri : null;

    private static bool IsHttp(Uri uri) => uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static IEnumerable<XElement> Children(XElement parent, string localName) =>
        parent.Elements().Where(e => e.Name.LocalName == localName);

    private static string? Attr(XElement element, string name) =>
        element.Attribute(name)?.Value is { } value && value.Trim().Length > 0 ? value.Trim() : null;

    private static string? Text(XElement? element) =>
        element?.Value.Trim() is { Length: > 0 } text ? text : null;

    private static long? ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static long? Positive(long? value) => value is > 0 ? value : null;

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result > 0 ? result : null;

    /// <summary>"30000/1001" → 29.97; "25" → 25.</summary>
    private static double? ParseFrameRate(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var parts = value.Split('/');
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) || numerator <= 0)
        {
            return null;
        }

        if (parts.Length == 1)
        {
            return numerator;
        }

        return parts.Length == 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) && denominator > 0
            ? numerator / denominator
            : null;
    }
}
