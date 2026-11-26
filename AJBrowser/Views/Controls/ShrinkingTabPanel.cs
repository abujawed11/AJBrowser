using System.Windows;
using System.Windows.Controls;

namespace AJBrowser.Views.Controls;

/// <summary>
/// Lays tabs out in one row. Every tab gets the same width: <see cref="MaxTabWidth"/> when there's room,
/// shrinking as more tabs open, down to <see cref="MinTabWidth"/>. Beyond that the extra tabs are clipped
/// (still reachable with Ctrl+Tab / Ctrl+1…9). <see cref="ReservedWidth"/> keeps room for the "+" button.
/// </summary>
public sealed class ShrinkingTabPanel : Panel
{
    private double _tabWidth;

    public ShrinkingTabPanel()
    {
        ClipToBounds = true;
    }

    public double MinTabWidth { get; set; } = 60;
    public double MaxTabWidth { get; set; } = 240;
    public double ReservedWidth { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = InternalChildren.Count;
        if (count == 0)
        {
            return new Size(0, 0);
        }

        var room = double.IsInfinity(availableSize.Width)
            ? double.PositiveInfinity
            : Math.Max(0, availableSize.Width - ReservedWidth);

        _tabWidth = Math.Clamp(room / count, MinTabWidth, MaxTabWidth);

        double height = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(_tabWidth, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(Math.Min(_tabWidth * count, room), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Arrange(new Rect(x, 0, _tabWidth, finalSize.Height));
            x += _tabWidth;
        }

        return finalSize;
    }
}
