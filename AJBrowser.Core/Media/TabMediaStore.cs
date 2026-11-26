using System.Text.Json;
using AJBrowser.Core.Security;

namespace AJBrowser.Core.Media;

/// <summary>One video (or audio file, or stream) found in a tab. Immutable snapshot; the store replaces it on change.</summary>
public sealed record DetectedMedia
{
    public required int Id { get; init; }
    public required MediaKind Kind { get; init; }

    /// <summary>De-duplication identity (scheme, host, path).</summary>
    public required string Key { get; init; }

    /// <summary>The newest full address (tokens included). Memory only: never log or persist it (use <see cref="DisplayUrl"/>).</summary>
    public required Uri Url { get; init; }

    /// <summary>"MP4", "WebM", "HLS"…, when known.</summary>
    public string? Container { get; init; }

    public bool IsAudioOnly { get; init; }

    /// <summary>The whole file's size, when the server told us.</summary>
    public long? TotalBytes { get; init; }

    /// <summary>From the page's &lt;video&gt; element (untrusted hint) once its currentSrc matched this file.</summary>
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? DurationSeconds { get; init; }

    /// <summary>
    /// A complete file we're confident about (plan §6.2 step 4): the page's player confirmed it, or a native player
    /// loaded it and it's big enough. Unconfirmed ones are shown as "other media" (or not at all).
    /// </summary>
    public bool IsConfirmed { get; init; }

    /// <summary>The page's own player reported playing exactly this file (see <see cref="VideoHint"/>).</summary>
    public bool ConfirmedByPage { get; init; }

    /// <summary>How the browser loaded it (WebView2 resource context), from the latest request.</summary>
    public string? ResourceContext { get; init; }

    /// <summary>Headers to repeat when downloading it ourselves (allowlisted; memory only).</summary>
    public RequestContext? Request { get; init; }

    /// <summary>Found after a same-page navigation (a single-page app moved on): may belong to the new "page".</summary>
    public bool IsNewSinceSpaNavigation { get; init; }

    public int RequestCount { get; init; }
    public DateTime FirstSeenUtc { get; init; }
    public DateTime LastSeenUtc { get; init; }

    /// <summary>For logs and the picker's tooltip: no query, tokens in the path shortened (spike F12).</summary>
    public string DisplayUrl => Redactor.MediaUrl(Url);

    public string Host => Url.Host;

    /// <summary>"1280×720", or null when the size is unknown (labels come only from data, plan §6.2 step 6).</summary>
    public string? Resolution => Width is > 0 && Height is > 0 ? $"{Width}×{Height}" : null;
}

/// <summary>
/// What the page's hint script reports about one &lt;video&gt; element (plan Phase 5). <b>Untrusted</b>: any page can
/// send any message, so it's parsed strictly here, only enriches labels, and never triggers an action by itself (§5.8).
/// </summary>
public sealed record VideoHint(Uri? CurrentSrc, bool IsBlob, int? Width, int? Height, double? DurationSeconds)
{
    /// <summary>Messages larger than this are dropped unread.</summary>
    public const int MaxMessageLength = 4096;

    private const int MaxDimension = 16384;
    private const double MaxDuration = 7 * 24 * 3600;

    /// <summary>
    /// Parses <c>{"type":"ajb-video","currentSrc":"…","videoWidth":1280,"videoHeight":720,"duration":12.5}</c>.
    /// Anything malformed, oversized or out of range gives null (or drops just that field).
    /// </summary>
    public static VideoHint? TryParse(string? json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > MaxMessageLength)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "ajb-video")
            {
                return null;
            }

            var source = root.TryGetProperty("currentSrc", out var src) && src.ValueKind == JsonValueKind.String ? src.GetString() : null;
            var isBlob = source?.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) == true;
            Uri? uri = null;
            if (!isBlob && source is { Length: > 0 and <= 2048 } && Uri.TryCreate(source, UriKind.Absolute, out var parsed)
                && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp))
            {
                uri = parsed;
            }

            return new VideoHint(uri, isBlob, Dimension(root, "videoWidth"), Dimension(root, "videoHeight"), Duration(root));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? Dimension(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
        && number is > 0 and <= MaxDimension ? number : null;

    private static double? Duration(JsonElement root) =>
        root.TryGetProperty("duration", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var seconds)
        && double.IsFinite(seconds) && seconds is > 0 and <= MaxDuration ? seconds : null;
}

/// <summary>
/// The videos found in one tab, for the current page (plan §6.2 steps 3, 4, 7). Feed it every response the tab sees;
/// it keeps one entry per file, decides which are complete videos, and forgets everything when a new page loads.
/// Not thread-safe: use it from one thread (the Browser layer calls it on the UI thread; it's cheap).
/// </summary>
public sealed class TabMediaStore
{
    /// <summary>Below this a "complete" file is more likely a preview, a hover snippet or a piece of a stream.</summary>
    public const long MinConfirmedBytes = 1024 * 1024;

    /// <summary>A page can't flood us: beyond this many entries new ones are ignored.</summary>
    public const int MaxItems = 200;

    /// <summary>How a native player (not page script) loads a file: these requests point at a real, whole file.</summary>
    private static readonly HashSet<string> NativeContexts = new(StringComparer.OrdinalIgnoreCase) { "Media", "Document" };

    private readonly Dictionary<string, DetectedMedia> _byKey = new(StringComparer.Ordinal);
    private readonly Func<DateTime> _utcNow;
    private int _nextId = 1;
    private bool _afterSpaNavigation;

    public TabMediaStore(Func<DateTime>? utcNow = null) => _utcNow = utcNow ?? (() => DateTime.UtcNow);

    /// <summary>Changes every time the visible list changes (cheap "did anything happen?" check for throttled UI updates).</summary>
    public int Version { get; private set; }

    /// <summary>Everything found on this page, oldest first (segments are never stored).</summary>
    public IReadOnlyList<DetectedMedia> Items => _byKey.Values.OrderBy(m => m.Id).ToList();

    /// <summary>What the picker shows: confirmed whole files, and streams (HLS/DASH, "supported soon" until Phase 6/7).</summary>
    public IReadOnlyList<DetectedMedia> Visible => _byKey.Values.Where(IsVisible).OrderBy(m => m.Id).ToList();

    /// <summary>The badge number on the toolbar button.</summary>
    public int VisibleCount => _byKey.Values.Count(IsVisible);

    /// <summary>A new document is loading: the old page's videos are gone.</summary>
    public void Reset()
    {
        if (_byKey.Count > 0)
        {
            _byKey.Clear();
            Version++;
        }

        _afterSpaNavigation = false;
    }

    /// <summary>A same-document navigation (single-page apps): keep the list, but flag what's found from now on.</summary>
    public void MarkSpaNavigation() => _afterSpaNavigation = true;

    /// <summary>Adds what a response showed. Returns true if the visible list changed.</summary>
    public bool Add(MediaObservation observation, RequestContext? request = null)
    {
        var classification = MediaClassifier.Classify(observation);
        if (classification.Kind is MediaKind.None or MediaKind.Segment)
        {
            return false;
        }

        var now = _utcNow();
        var key = MediaClassifier.KeyOf(observation.Url);
        var total = MediaClassifier.TotalSize(observation);
        var visibleBefore = VisibleCount;

        if (_byKey.TryGetValue(key, out var existing))
        {
            // Another range request of the same file, a token refresh, or a live playlist being re-fetched (spike F1):
            // one entry, newest address (tokens expire), largest size seen.
            var updated = existing with
            {
                Url = observation.Url,
                TotalBytes = Max(existing.TotalBytes, total),
                ResourceContext = observation.ResourceContext ?? existing.ResourceContext,
                Request = request ?? existing.Request,
                RequestCount = existing.RequestCount + 1,
                LastSeenUtc = now,
            };
            updated = updated with { IsConfirmed = Confirm(updated) };
            _byKey[key] = updated;
            return Bump(existing, updated, visibleBefore);
        }

        if (_byKey.Count >= MaxItems)
        {
            return false;
        }

        var item = new DetectedMedia
        {
            Id = _nextId++,
            Kind = classification.Kind,
            Key = key,
            Url = observation.Url,
            Container = classification.Container,
            IsAudioOnly = classification.IsAudioOnly,
            TotalBytes = total,
            ResourceContext = observation.ResourceContext,
            Request = request,
            IsNewSinceSpaNavigation = _afterSpaNavigation,
            RequestCount = 1,
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };
        item = item with { IsConfirmed = Confirm(item) };
        _byKey[key] = item;
        if (IsVisible(item))
        {
            Version++;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The page's player reported a &lt;video&gt; (untrusted, see <see cref="VideoHint"/>). If its address matches a
    /// file we saw, that file gets its resolution and duration and counts as a confirmed video. Returns true if the
    /// visible list changed. Hints never add entries: only real network responses do.
    /// </summary>
    public bool ApplyHint(VideoHint hint)
    {
        if (hint.CurrentSrc is null || !_byKey.TryGetValue(MediaClassifier.KeyOf(hint.CurrentSrc), out var existing) || existing.Kind != MediaKind.Direct)
        {
            return false;
        }

        var visibleBefore = VisibleCount;
        var updated = existing with
        {
            Width = hint.Width ?? existing.Width,
            Height = hint.Height ?? existing.Height,
            DurationSeconds = hint.DurationSeconds ?? existing.DurationSeconds,
            ConfirmedByPage = true,
            IsConfirmed = true,
        };
        _byKey[existing.Key] = updated;
        return Bump(existing, updated, visibleBefore);
    }

    public DetectedMedia? Find(int id) => _byKey.Values.FirstOrDefault(m => m.Id == id);

    private bool Bump(DetectedMedia before, DetectedMedia after, int visibleBefore)
    {
        var changed = VisibleCount != visibleBefore
                      || (IsVisible(after) && (before.TotalBytes != after.TotalBytes || before.Width != after.Width
                                               || before.Height != after.Height || before.DurationSeconds != after.DurationSeconds));
        if (changed)
        {
            Version++;
        }

        return changed;
    }

    /// <summary>
    /// A direct file is a complete video if a native player (or a plain navigation) loaded it and it's at least
    /// <see cref="MinConfirmedBytes"/>, or the page's player confirmed it (hint, kept once set). A file fetched by page
    /// script (fetch/XHR) is how streaming players load pieces, e.g. the byte ranges of one big fMP4 behind an HLS
    /// playlist that look exactly like a whole MP4 (spike F1), so it is never confirmed by size alone.
    /// </summary>
    private bool Confirm(DetectedMedia item)
    {
        if (item.Kind != MediaKind.Direct)
        {
            return false;
        }

        if (item.ConfirmedByPage)
        {
            return true;
        }

        var native = item.ResourceContext is null || NativeContexts.Contains(item.ResourceContext);
        var bigEnough = item.TotalBytes is >= MinConfirmedBytes;
        var streamOnPage = _byKey.Values.Any(m => m.Kind is MediaKind.Hls or MediaKind.Dash);
        return native && bigEnough && !(streamOnPage && item.ResourceContext is null);
    }

    private static bool IsVisible(DetectedMedia item) => item.Kind is MediaKind.Hls or MediaKind.Dash || item.IsConfirmed;

    private static long? Max(long? a, long? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
}
