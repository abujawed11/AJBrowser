using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows.Media;
using AJBrowser.Core.Logging;
using AJBrowser.Core.Persistence;
using AJBrowser.Core.Security;

namespace AJBrowser.Services;

/// <summary>
/// Remembers each site's icon so lists (History, Bookmarks, the bookmarks bar, suggestions) and tabs can show it.
/// <para>
/// Tabs hand over the icon WebView2 gives them, which is only 16 px and looks blurry on a scaled display (125%,
/// 150%). So, once per site and session, if the stored icon is smaller than <see cref="FaviconRules.GoodSize"/>, the
/// icon file itself is downloaded (no cookies, at most 256 KB) and its sharpest picture is kept (up to 64 px).
/// All icons are kept in memory (they're tiny) and saved to the database in the background. Use from the UI thread.
/// </para>
/// </summary>
public sealed class FaviconService
{
    private const int MaxDownloadBytes = 256 * 1024;

    /// <summary>One shared client: no cookies (an icon needs none), short timeout, a few redirects.</summary>
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        UseCookies = false,
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = TimeSpan.FromSeconds(8),
    };

    private readonly FaviconRepository _repository;
    private readonly AppLogger _log;
    private readonly Dictionary<string, byte[]> _png = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImageSource?> _decoded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _downloadAttempted = new(StringComparer.Ordinal);
    private Task _writes = Task.CompletedTask;

    public FaviconService(FaviconRepository repository, AppLogger log)
    {
        _repository = repository;
        _log = log;
    }

    /// <summary>A site got a new or sharper icon (tabs and the bookmarks bar refresh).</summary>
    public event EventHandler? Changed;

    public async Task LoadAsync()
    {
        try
        {
            foreach (var (host, png) in await _repository.GetAllAsync())
            {
                _png[host] = png;
            }

            _log.Info($"Loaded {_png.Count} site icons");
        }
        catch (Exception ex)
        {
            _log.Warn("Could not load site icons", ex);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The best known icon for the site of <paramref name="url"/>, or null (then views show a glyph).</summary>
    public ImageSource? Get(string? url)
    {
        if (FaviconRepository.KeyFor(url) is not { } host || !_png.TryGetValue(host, out var png))
        {
            return null;
        }

        if (!_decoded.TryGetValue(host, out var image))
        {
            image = FaviconImages.Decode(png);
            _decoded[host] = image; // a broken image is remembered as null, so it isn't decoded again
        }

        return image;
    }

    /// <summary>
    /// A tab received an icon for <paramref name="pageUrl"/>. Kept if it's sharper than (or different from) the stored
    /// one; if what we have is still small, the icon file at <paramref name="iconUrl"/> is fetched for a sharper picture.
    /// </summary>
    public void Remember(string? pageUrl, byte[] png, string? iconUrl, string? userAgent)
    {
        if (FaviconRepository.KeyFor(pageUrl) is not { } host)
        {
            return;
        }

        Store(host, png);

        if (FaviconRules.PngWidth(_png.GetValueOrDefault(host)) < FaviconRules.GoodSize
            && Uri.TryCreate(iconUrl, UriKind.Absolute, out var iconUri)
            && (iconUri.Scheme == Uri.UriSchemeHttps || iconUri.Scheme == Uri.UriSchemeHttp)
            && _downloadAttempted.Add(host))
        {
            _ = DownloadSharperAsync(host, iconUri, userAgent);
        }
    }

    private void Store(string host, byte[] png)
    {
        if (!FaviconRules.ShouldReplace(_png.GetValueOrDefault(host), png))
        {
            return;
        }

        _png[host] = png;
        _decoded.Remove(host);
        var now = DateTime.UtcNow;
        _writes = _writes.ContinueWith(async _ =>
        {
            try
            {
                await _repository.SaveAsync(host, png, now);
            }
            catch (Exception ex)
            {
                _log.Debug($"Could not save a site icon: {ex.GetType().Name}");
            }
        }, TaskScheduler.Default).Unwrap();

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Downloads the page's icon file and keeps its sharpest picture. If that fails or is still small, the site's
    /// standard /favicon.ico is tried too, except when the page's icon is an SVG: WPF can't draw SVG, and SVG icons
    /// often change colour with the theme (GitHub's is white in dark mode while its favicon.ico is black and would
    /// vanish on a dark background), so WebView2's correctly coloured 16 px copy is kept instead.
    /// Never throws; failures just keep the small icon.
    /// </summary>
    private async Task DownloadSharperAsync(string host, Uri iconUri, string? userAgent)
    {
        if (iconUri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var (best, isSvg) = await DownloadBestPngAsync(iconUri, userAgent);
        if (best is not null)
        {
            Store(host, best); // back on the UI thread here
        }

        var rootIcon = new Uri(iconUri.GetLeftPart(UriPartial.Authority) + "/favicon.ico");
        if (!isSvg && iconUri != rootIcon && FaviconRules.PngWidth(_png.GetValueOrDefault(host)) < FaviconRules.GoodSize
            && (await DownloadBestPngAsync(rootIcon, userAgent)).Png is { } fromRoot)
        {
            Store(host, fromRoot);
        }
    }

    /// <summary>The sharpest picture in the icon file at <paramref name="iconUri"/> as PNG (or null), and whether the file was an SVG.</summary>
    private async Task<(byte[]? Png, bool IsSvg)> DownloadBestPngAsync(Uri iconUri, string? userAgent)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, iconUri);
            if (!string.IsNullOrEmpty(userAgent))
            {
                request.Headers.UserAgent.TryParseAdd(userAgent); // the browser's own user agent, like the page's request
            }

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (string.Equals(response.Content.Headers.ContentType?.MediaType, "image/svg+xml", StringComparison.OrdinalIgnoreCase))
            {
                return (null, true);
            }

            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxDownloadBytes)
            {
                return (null, false);
            }

            await using var body = await response.Content.ReadAsStreamAsync();
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await body.ReadAsync(chunk)) > 0)
            {
                if (buffer.Length + read > MaxDownloadBytes)
                {
                    return (null, false); // bigger than any real favicon
                }

                buffer.Write(chunk, 0, read);
            }

            var data = buffer.ToArray();
            return (await Task.Run(() => FaviconImages.ToBestPng(data)), false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            _log.Debug($"Sharper icon not available for {Redactor.Url(iconUri)}: {ex.GetType().Name}");
            return (null, false);
        }
    }
}
