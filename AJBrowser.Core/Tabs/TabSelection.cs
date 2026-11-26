namespace AJBrowser.Core.Tabs;

/// <summary>Pure tab-index rules, kept out of the view model so they can be unit-tested.</summary>
public static class TabSelection
{
    /// <summary>
    /// The index of the tab to select after the tab at <paramref name="closedIndex"/> is removed.
    /// <list type="bullet">
    /// <item>Closing another tab keeps the same tab selected (its index shifts left if the closed tab was before it).</item>
    /// <item>Closing the selected tab selects its right neighbour, or the left one if it was last.</item>
    /// </list>
    /// Returns -1 when no tabs remain.
    /// </summary>
    public static int IndexAfterClose(int closedIndex, int selectedIndex, int countBeforeClose)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(closedIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(closedIndex, countBeforeClose);

        var countAfter = countBeforeClose - 1;
        if (countAfter == 0)
        {
            return -1;
        }

        if (closedIndex != selectedIndex)
        {
            return selectedIndex > closedIndex ? selectedIndex - 1 : selectedIndex;
        }

        // The right neighbour slides into the closed tab's index; if it was the last tab, take the new last one.
        return Math.Min(closedIndex, countAfter - 1);
    }

    /// <summary>Next (+1) or previous (-1) tab, wrapping around like Ctrl+Tab in other browsers.</summary>
    public static int Cycle(int selectedIndex, int count, int step)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        return ((selectedIndex + step) % count + count) % count;
    }

    /// <summary>
    /// Ctrl+1..Ctrl+8 select that tab (if it exists); Ctrl+9 always selects the last tab. Returns -1 if there's no such tab.
    /// </summary>
    public static int ForNumberKey(int number, int count)
    {
        if (number is < 1 or > 9 || count < 1)
        {
            return -1;
        }

        if (number == 9)
        {
            return count - 1;
        }

        return number <= count ? number - 1 : -1;
    }
}
