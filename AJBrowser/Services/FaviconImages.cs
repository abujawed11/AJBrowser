using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AJBrowser.Core.Persistence;

namespace AJBrowser.Services;

/// <summary>Decoding and picking site icons with WPF's image decoders (PNG, ICO, GIF, JPEG, BMP; not SVG).</summary>
public static class FaviconImages
{
    /// <summary>
    /// PNG bytes → a frozen image (frozen = immutable, so WPF can share it). Decoded at its real size: forcing a
    /// bigger size here would only blur it. Very large icons are decoded at <see cref="FaviconRules.MaxStoredSize"/>.
    /// </summary>
    public static ImageSource? Decode(byte[] png)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; // read everything now; the stream can be closed
            if (FaviconRules.PngWidth(png) > FaviconRules.MaxStoredSize)
            {
                image.DecodePixelWidth = FaviconRules.MaxStoredSize;
            }

            image.StreamSource = new MemoryStream(png);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException or InvalidOperationException or FileFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Picks the sharpest picture in a downloaded icon file (an .ico usually holds 16, 32, 48… px versions) and
    /// returns it as PNG, scaled down to at most <see cref="FaviconRules.MaxStoredSize"/>. Null if it can't be decoded.
    /// Safe to call on a background thread.
    /// </summary>
    public static byte[]? ToBestPng(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

            // Prefer the largest square picture; ICO files often contain several sizes.
            BitmapSource? best = null;
            foreach (var frame in decoder.Frames)
            {
                if (frame.PixelWidth > 0 && frame.PixelWidth == frame.PixelHeight
                    && (best is null || frame.PixelWidth > best.PixelWidth))
                {
                    best = frame;
                }
            }

            best ??= decoder.Frames.Count > 0 ? decoder.Frames[0] : null;
            if (best is null || best.PixelWidth == 0)
            {
                return null;
            }

            if (best.PixelWidth > FaviconRules.MaxStoredSize)
            {
                var scale = (double)FaviconRules.MaxStoredSize / best.PixelWidth;
                best = new TransformedBitmap(best, new ScaleTransform(scale, scale));
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(best));
            using var output = new MemoryStream();
            encoder.Save(output);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException or InvalidOperationException or FileFormatException or OverflowException)
        {
            return null; // SVG, a broken file, or something that isn't an image
        }
    }
}
