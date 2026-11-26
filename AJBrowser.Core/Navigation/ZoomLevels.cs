namespace AJBrowser.Core.Navigation;

/// <summary>Page zoom steps for Ctrl+Plus / Ctrl+Minus (the same steps Chrome and Edge use).</summary>
public static class ZoomLevels
{
    public static IReadOnlyList<double> Steps { get; } =
        [0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0, 5.0];

    public const double Default = 1.0;

    /// <summary>The next step above <paramref name="current"/> (stays at the maximum).</summary>
    public static double ZoomIn(double current)
    {
        foreach (var step in Steps)
        {
            if (step > current + 0.001)
            {
                return step;
            }
        }

        return Steps[^1];
    }

    /// <summary>The next step below <paramref name="current"/> (stays at the minimum).</summary>
    public static double ZoomOut(double current)
    {
        for (var i = Steps.Count - 1; i >= 0; i--)
        {
            if (Steps[i] < current - 0.001)
            {
                return Steps[i];
            }
        }

        return Steps[0];
    }

    public static bool IsDefault(double zoom) => Math.Abs(zoom - Default) < 0.001;

    /// <summary>"125%". Works for any value, including Ctrl+mouse-wheel zoom between steps.</summary>
    public static string ToPercentText(double zoom) => $"{Math.Round(zoom * 100):0}%";
}
