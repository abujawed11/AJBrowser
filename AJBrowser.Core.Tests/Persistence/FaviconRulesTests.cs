using AJBrowser.Core.Persistence;

namespace AJBrowser.Core.Tests.Persistence;

public sealed class FaviconRulesTests
{
    /// <summary>Just enough of a PNG for the header checks: signature + IHDR chunk with the given size.</summary>
    private static byte[] Png(int width, byte marker = 0)
    {
        byte[] bytes =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,       // signature
            0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
            (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
            8, 6, 0, 0, 0, marker,
        ];
        return bytes;
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(180)]
    public void Reads_png_width(int width) => Assert.Equal(width, FaviconRules.PngWidth(Png(width)));

    [Fact]
    public void Non_png_has_no_width()
    {
        Assert.Equal(0, FaviconRules.PngWidth(null));
        Assert.Equal(0, FaviconRules.PngWidth([1, 2, 3]));
        Assert.Equal(0, FaviconRules.PngWidth(new byte[40]));
    }

    [Fact]
    public void Bigger_icons_win_and_smaller_never_replace()
    {
        Assert.True(FaviconRules.ShouldReplace(null, Png(16)));
        Assert.True(FaviconRules.ShouldReplace(Png(16), Png(32)));
        Assert.False(FaviconRules.ShouldReplace(Png(32), Png(16)));
    }

    [Fact]
    public void Same_size_replaces_only_when_different()
    {
        Assert.False(FaviconRules.ShouldReplace(Png(32), Png(32)));
        Assert.True(FaviconRules.ShouldReplace(Png(32), Png(32, marker: 7)));
    }

    [Fact]
    public void Garbage_never_replaces()
    {
        Assert.False(FaviconRules.ShouldReplace(null, [1, 2, 3]));
        Assert.True(FaviconRules.ShouldReplace([1, 2, 3], Png(16))); // a broken stored icon is replaced
    }
}
