namespace AJBrowser.Core.Tabs;

/// <summary>What we remember about a closed tab so Ctrl+Shift+T can bring it back.</summary>
public sealed record ClosedTab(Uri Url, string Title, int Index);

/// <summary>
/// The most recently closed tabs, newest first, capped at <see cref="Capacity"/> (oldest dropped).
/// Kept in memory only; session restore (Phase 3) persists tabs separately.
/// </summary>
public sealed class ClosedTabStack
{
    public const int DefaultCapacity = 25;

    private readonly LinkedList<ClosedTab> _items = new();

    public ClosedTabStack(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    public int Capacity { get; }

    public int Count => _items.Count;

    public void Push(ClosedTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        _items.AddFirst(tab);
        if (_items.Count > Capacity)
        {
            _items.RemoveLast();
        }
    }

    public bool TryPop(out ClosedTab? tab)
    {
        if (_items.First is null)
        {
            tab = null;
            return false;
        }

        tab = _items.First.Value;
        _items.RemoveFirst();
        return true;
    }
}
