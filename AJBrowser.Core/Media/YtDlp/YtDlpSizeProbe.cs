using System.Net;
using System.Net.Http.Headers;

namespace AJBrowser.Core.Media.YtDlp;

/// <summary>
/// Fills in file sizes yt-dlp doesn't report (Instagram gives neither sizes nor the video's length, so nothing can be
/// estimated) by asking each format's server for the size: a HEAD request, or a one-byte ranged GET when HEAD isn't
/// answered with a length. Without cookies, https only, at most <see cref="MaxRequests"/> formats, a few seconds each,
/// in parallel. Formats that already have a size are left alone, so YouTube and Facebook cost nothing. Failures just
/// leave the size unknown.
/// </summary>
public static class YtDlpSizeProbe
{
    public const int MaxRequests = 16;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static async Task<YtDlpInfo> FillAsync(HttpClient http, YtDlpInfo info, string? userAgent, CancellationToken cancellationToken = default)
    {
        var targets = info.Formats
            .Select((f, i) => (Format: f, Index: i))
            .Where(x => x.Format.FileSize is null && x.Format.FileSizeApprox is null && !x.Format.IsJunk && !x.Format.HasDrm
                        && x.Format.Url is { Scheme: "https" }
                        && x.Format.Protocol is null or "https" or "http") // not HLS/DASH manifests: their address isn't the file
            .Take(MaxRequests)
            .ToList();
        if (targets.Count == 0)
        {
            return info;
        }

        var sizes = await Task.WhenAll(targets.Select(t => SizeOfAsync(http, t.Format.Url!, userAgent, cancellationToken))).ConfigureAwait(false);
        var formats = info.Formats.ToArray();
        for (var i = 0; i < targets.Count; i++)
        {
            if (sizes[i] is { } size)
            {
                formats[targets[i].Index] = formats[targets[i].Index] with { FileSize = size };
            }
        }

        return info with { Formats = formats };
    }

    /// <summary>The resource's size in bytes, or null.</summary>
    internal static async Task<long?> SizeOfAsync(HttpClient http, Uri url, string? userAgent, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using (var head = Request(HttpMethod.Head, url, userAgent))
            using (var response = await http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
            {
                if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength is > 0 and var length)
                {
                    return length;
                }
            }

            using var get = Request(HttpMethod.Get, url, userAgent);
            get.Headers.Range = new RangeHeaderValue(0, 0);
            using var ranged = await http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return ranged.StatusCode == HttpStatusCode.PartialContent && ranged.Content.Headers.ContentRange?.Length is > 0 and var total ? total : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return null;
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, Uri url, string? userAgent)
    {
        var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }

        return request;
    }
}
