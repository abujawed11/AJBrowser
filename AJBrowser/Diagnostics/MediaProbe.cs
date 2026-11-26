using System.IO;
using System.Text;
using AJBrowser.Core.Security;
using Microsoft.Web.WebView2.Core;

namespace AJBrowser.Diagnostics;

internal enum SpikeMediaKind
{
    Other,
    DirectMedia,
    HlsManifest,
    DashManifest,
    Segment,
}

internal sealed record MediaObservation(
    string RedactedUrl,
    int Status,
    string? ContentType,
    string? ContentLength,
    string? ContentRange,
    SpikeMediaKind Kind);

internal sealed record ManifestBodyResult(string RedactedUrl, bool Success, int Bytes, string Detail);

/// <summary>
/// Spike for V1/V2: watches WebResourceResponseReceived and records what WebView2 exposes for media.
/// The classifier here is intentionally rough; the real one is built and unit-tested in Phase 5.
/// </summary>
internal sealed class MediaProbe
{
    private CoreWebView2? _core;

    public Action<string>? Log { get; set; }

    public List<MediaObservation> Observations { get; } = [];
    public List<ManifestBodyResult> ManifestBodies { get; } = [];
    public int TotalResponses { get; private set; }
    public int RedirectResponses { get; private set; }
    public int PartialResponses { get; private set; }
    public int RequestsWithCookieHeaderVisible { get; private set; }
    public int RequestsWithAuthorizationHeaderVisible { get; private set; }

    /// <summary>When false, responses are only counted (used during the blocking benchmark).</summary>
    public bool LogMedia { get; set; } = true;

    public void Attach(CoreWebView2 core)
    {
        _core = core;
        core.WebResourceResponseReceived += OnResponseReceived;
    }

    public void Detach()
    {
        if (_core is not null)
        {
            _core.WebResourceResponseReceived -= OnResponseReceived;
            _core = null;
        }
    }

    public void Reset()
    {
        Observations.Clear();
        ManifestBodies.Clear();
        TotalResponses = RedirectResponses = PartialResponses = 0;
        RequestsWithCookieHeaderVisible = RequestsWithAuthorizationHeaderVisible = 0;
    }

    internal static SpikeMediaKind Classify(string url, string? contentType)
    {
        var type = contentType?.Split(';')[0].Trim().ToLowerInvariant() ?? string.Empty;
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath.ToLowerInvariant() : string.Empty;

        if (type is "application/vnd.apple.mpegurl" or "application/x-mpegurl" or "audio/mpegurl" or "audio/x-mpegurl"
            || path.EndsWith(".m3u8", StringComparison.Ordinal))
        {
            return SpikeMediaKind.HlsManifest;
        }

        if (type == "application/dash+xml" || path.EndsWith(".mpd", StringComparison.Ordinal))
        {
            return SpikeMediaKind.DashManifest;
        }

        if (type == "video/mp2t" || path.EndsWith(".ts", StringComparison.Ordinal) || path.EndsWith(".m4s", StringComparison.Ordinal))
        {
            return SpikeMediaKind.Segment;
        }

        if (type.StartsWith("video/", StringComparison.Ordinal) || type.StartsWith("audio/", StringComparison.Ordinal))
        {
            return SpikeMediaKind.DirectMedia;
        }

        if (type is "application/octet-stream" or "binary/octet-stream" or ""
            && (path.EndsWith(".mp4", StringComparison.Ordinal) || path.EndsWith(".webm", StringComparison.Ordinal)))
        {
            return SpikeMediaKind.DirectMedia;
        }

        return SpikeMediaKind.Other;
    }

    private async void OnResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        try
        {
            TotalResponses++;
            var status = e.Response.StatusCode;
            if (status is >= 300 and < 400)
            {
                RedirectResponses++;
            }

            if (status == 206)
            {
                PartialResponses++;
            }

            // Security check: does this event expose credentials? We only count, never read values.
            if (e.Request.Headers.Contains("Cookie"))
            {
                RequestsWithCookieHeaderVisible++;
            }

            if (e.Request.Headers.Contains("Authorization"))
            {
                RequestsWithAuthorizationHeaderVisible++;
            }

            var url = e.Request.Uri;
            var contentType = Header(e.Response.Headers, "Content-Type");
            var kind = Classify(url, contentType);
            if (kind == SpikeMediaKind.Other)
            {
                return;
            }

            var observation = new MediaObservation(
                Redactor.Url(url),
                status,
                contentType,
                Header(e.Response.Headers, "Content-Length"),
                Header(e.Response.Headers, "Content-Range"),
                kind);
            Observations.Add(observation);

            if (LogMedia)
            {
                Log?.Invoke($"[media] {kind} {status} {contentType} len={observation.ContentLength} range={observation.ContentRange} {observation.RedactedUrl}");
            }

            if (kind is SpikeMediaKind.HlsManifest or SpikeMediaKind.DashManifest)
            {
                await ReadManifestBodyAsync(e, observation.RedactedUrl);
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[media] handler error: {ex.GetType().Name}: {Redactor.Text(ex.Message)}");
        }
    }

    private async Task ReadManifestBodyAsync(CoreWebView2WebResourceResponseReceivedEventArgs e, string redactedUrl)
    {
        try
        {
            using var stream = await e.Response.GetContentAsync();
            if (stream is null)
            {
                ManifestBodies.Add(new ManifestBodyResult(redactedUrl, false, 0, "GetContentAsync returned null"));
                Log?.Invoke($"[manifest] body NULL for {redactedUrl}");
                return;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = await reader.ReadToEndAsync();
            var firstLine = text.TrimStart().Split('\n').FirstOrDefault()?.Trim() ?? string.Empty;
            if (firstLine.Length > 60)
            {
                firstLine = firstLine[..60] + "…";
            }

            ManifestBodies.Add(new ManifestBodyResult(redactedUrl, true, text.Length, $"first line: `{firstLine}`"));
            Log?.Invoke($"[manifest] body OK ({text.Length} chars) {redactedUrl}");
        }
        catch (Exception ex)
        {
            ManifestBodies.Add(new ManifestBodyResult(redactedUrl, false, 0, $"{ex.GetType().Name}: {Redactor.Text(ex.Message)}"));
            Log?.Invoke($"[manifest] body FAILED for {redactedUrl}: {ex.GetType().Name}");
        }
    }

    private static string? Header(CoreWebView2HttpResponseHeaders headers, string name) =>
        headers.Contains(name) ? headers.GetHeader(name) : null;
}
