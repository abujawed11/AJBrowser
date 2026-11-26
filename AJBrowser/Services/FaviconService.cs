using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AJBrowser.Core.Logging;
using AJBrowser.Core.Persistence;

namespace AJBrowser.Services;

/// <summary>
/// Remembers each site's icon so lists (History, Bookmarks, the bookmarks bar, suggestions) can show it, not only
/// the open tab. Tabs hand over every icon they receive; all icons are kept in memory (they're tiny) and saved to
/// the database in the background. Lookups are instant, so views can ask for icons while building their rows.
/// Use it from the UI thread.
/// </summary>
public sealed class FaviconService
{
    private readonly FaviconRepository _repository;
    private readonly AppLogger _log;
    private readonly Dictionary<string, byte[]> _png = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImageSource?> _decoded = new(StringComparer.Ordinal);
    private Task _writes = Task.CompletedTask;

    public FaviconService(FaviconRepository repository, AppLogger log)
    {
        _repository = repository;
        _log = log;
    }

    /// <summary>A site got a new or different icon (the bookmarks bar refreshes).</summary>
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

    /// <summary>The icon for the site of <paramref name="url"/>, or null (then views show a globe).</summary>
    public ImageSource? Get(string? url)
    {
        if (FaviconRepository.KeyFor(url) is not { } host || !_png.TryGetValue(host, out var png))
        {
            return null;
        }

        if (!_decoded.TryGetValue(host, out var image))
        {
            image = Decode(png);
            _decoded[host] = image; // a broken image is remembered as null, so it isn't decoded again
        }

        return image;
    }

    /// <summary>A tab received an icon for <paramref name="pageUrl"/>. Saved only if it's new or different.</summary>
    public void Remember(string? pageUrl, byte[] png)
    {
        if (FaviconRepository.KeyFor(pageUrl) is not { } host
            || png.Length == 0 || png.Length > FaviconRepository.MaxIconBytes
            || (_png.TryGetValue(host, out var existing) && existing.AsSpan().SequenceEqual(png)))
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

    /// <summary>PNG bytes → a small frozen image (frozen = immutable, so WPF can share it safely).</summary>
    public static ImageSource? Decode(byte[] png)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; // read everything now; the stream can be closed
            image.DecodePixelWidth = 32;                  // icons are shown at 16 px; 32 keeps them sharp at 200% scaling
            image.StreamSource = new MemoryStream(png);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
