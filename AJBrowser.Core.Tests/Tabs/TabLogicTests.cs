using AJBrowser.Core.Tabs;

namespace AJBrowser.Core.Tests.Tabs;

public class TabSelectionTests
{
    [Theory]
    // closed, selected, count -> expected
    [InlineData(2, 2, 5, 2)] // close the selected middle tab -> right neighbour (slides into index 2)
    [InlineData(4, 4, 5, 3)] // close the selected last tab -> new last tab
    [InlineData(0, 0, 3, 0)] // close the selected first tab -> its right neighbour
    [InlineData(1, 3, 5, 2)] // close a tab left of the selection -> selection index shifts left
    [InlineData(4, 1, 5, 1)] // close a tab right of the selection -> unchanged
    [InlineData(0, 0, 1, -1)] // close the only tab -> none left
    [InlineData(1, 0, 2, 0)]
    [InlineData(0, 1, 2, 0)]
    public void Index_after_close(int closed, int selected, int count, int expected)
    {
        Assert.Equal(expected, TabSelection.IndexAfterClose(closed, selected, count));
    }

    [Theory]
    [InlineData(-1, 0, 3)]
    [InlineData(3, 0, 3)]
    public void Index_after_close_rejects_bad_indexes(int closed, int selected, int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TabSelection.IndexAfterClose(closed, selected, count));
    }

    [Theory]
    [InlineData(0, 3, 1, 1)]
    [InlineData(2, 3, 1, 0)]  // wraps to the first tab
    [InlineData(0, 3, -1, 2)] // wraps to the last tab
    [InlineData(0, 1, 1, 0)]
    [InlineData(0, 1, -1, 0)]
    public void Cycle_wraps_around(int selected, int count, int step, int expected)
    {
        Assert.Equal(expected, TabSelection.Cycle(selected, count, step));
    }

    [Theory]
    [InlineData(1, 5, 0)]
    [InlineData(5, 5, 4)]
    [InlineData(8, 10, 7)]
    [InlineData(6, 5, -1)] // no 6th tab
    [InlineData(9, 5, 4)]  // Ctrl+9 = last tab
    [InlineData(9, 1, 0)]
    [InlineData(0, 5, -1)]
    [InlineData(1, 0, -1)]
    public void Number_keys(int number, int count, int expected)
    {
        Assert.Equal(expected, TabSelection.ForNumberKey(number, count));
    }
}

public class ClosedTabStackTests
{
    private static ClosedTab Tab(int n) => new(new Uri($"https://example.com/{n}"), $"Page {n}", n);

    [Fact]
    public void Pops_newest_first()
    {
        var stack = new ClosedTabStack();
        stack.Push(Tab(1));
        stack.Push(Tab(2));

        Assert.True(stack.TryPop(out var first));
        Assert.Equal(2, first!.Index);
        Assert.True(stack.TryPop(out var second));
        Assert.Equal(1, second!.Index);
        Assert.False(stack.TryPop(out var none));
        Assert.Null(none);
    }

    [Fact]
    public void Drops_the_oldest_beyond_capacity()
    {
        var stack = new ClosedTabStack(capacity: 3);
        for (var i = 1; i <= 5; i++)
        {
            stack.Push(Tab(i));
        }

        Assert.Equal(3, stack.Count);
        var popped = new List<int>();
        while (stack.TryPop(out var tab))
        {
            popped.Add(tab!.Index);
        }

        Assert.Equal([5, 4, 3], popped);
    }

    [Fact]
    public void Default_capacity_is_25()
    {
        Assert.Equal(25, new ClosedTabStack().Capacity);
    }

    [Fact]
    public void Rejects_zero_capacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClosedTabStack(0));
    }
}
