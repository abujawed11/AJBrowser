namespace AJBrowser.Core.Persistence;

/// <summary>
/// Which site icon to keep. Icons are stored as PNG; bigger (sharper) ones win, because a 16 px icon looks blurry
/// on a 125%/150% display while a 32 px one stays crisp.
/// </summary>
public static class FaviconRules
{
    /// <summary>Icons at least this wide look sharp at 16 px on displays up to 200% scaling.</summary>
    public const int GoodSize = 32;

    /// <summary>Icons are stored at most this wide (bigger ones are scaled down before saving).</summary>
    public const int MaxStoredSize = 64;

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The width from a PNG's header, or 0 if the bytes aren't a PNG.</summary>
    public static int PngWidth(byte[]? png)
    {
        // Signature (8 bytes), chunk length (4), "IHDR" (4), then width as a big-endian 32-bit number.
        if (png is null || png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(PngSignature)
            || png[12] != (byte)'I' || png[13] != (byte)'H' || png[14] != (byte)'D' || png[15] != (byte)'R')
        {
            return 0;
        }

        var width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        return width is > 0 and <= 4096 ? width : 0;
    }

    /// <summary>
    /// Should <paramref name="incoming"/> replace the stored icon? Yes if nothing is stored, if it's bigger, or if it's
    /// the same size but different (the site changed its icon). A smaller icon never replaces a bigger one.
    /// </summary>
    public static bool ShouldReplace(byte[]? existing, byte[] incoming)
    {
        var incomingWidth = PngWidth(incoming);
        if (incomingWidth == 0 || incoming.Length > FaviconRepository.MaxIconBytes)
        {
            return false;
        }

        var existingWidth = PngWidth(existing);
        if (existing is null || existingWidth == 0 || incomingWidth > existingWidth)
        {
            return true;
        }

        return incomingWidth == existingWidth && !existing.AsSpan().SequenceEqual(incoming);
    }
}
