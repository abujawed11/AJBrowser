using System.Collections.ObjectModel;
using AJBrowser.Core.Logging;
using AJBrowser.Core.Navigation;
using AJBrowser.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AJBrowser.ViewModels;

/// <summary>One row in the address-bar dropdown.</summary>
public sealed record SuggestionItem(string Url, string Title, bool IsBookmark)
{
    public string DisplayUrl => UrlDisplay.ForAddressBar(Url);

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? DisplayUrl : Title;

    /// <summary>Segoe Fluent Icons: a star for bookmarks, a clock for history.</summary>
    public string Glyph => IsBookmark ? "" : "";
}

/// <summary>
/// Address-bar suggestions from history and bookmarks. Typing restarts a short timer (debounce), then the
/// history query runs on a background thread and the results are ranked by <see cref="SuggestionRanker"/>.
/// </summary>
public sealed partial class SuggestionsViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(120);
    private const int CandidateLimit = 50;

    private readonly HistoryService _history;
    private readonly BookmarkService _bookmarks;
    private readonly AppLogger _log;
    private CancellationTokenSource? _pending;

    public SuggestionsViewModel(HistoryService history, BookmarkService bookmarks, AppLogger log)
    {
        _history = history;
        _bookmarks = bookmarks;
        _log = log;
    }

    public ObservableCollection<SuggestionItem> Items { get; } = [];

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    /// <summary>Highlighted row (arrow keys), or -1 for none: then Enter uses the typed text.</summary>
    [ObservableProperty]
    public partial int SelectedIndex { get; set; } = -1;

    public SuggestionItem? Selected => SelectedIndex >= 0 && SelectedIndex < Items.Count ? Items[SelectedIndex] : null;

    /// <summary>The typed text changed. Never throws.</summary>
    public async void Update(string text)
    {
        // async void: called like an event handler; everything below is caught.
        _pending?.Cancel();
        var query = text.Trim();
        if (query.Length == 0 || query.Contains("://", StringComparison.Ordinal) && query.Length > 200)
        {
            Close();
            return;
        }

        var cts = _pending = new CancellationTokenSource();
        try
        {
            await Task.Delay(Debounce, cts.Token);
            var fromHistory = await _history.FindSuggestionCandidatesAsync(query, CandidateLimit);
            cts.Token.ThrowIfCancellationRequested();

            var candidates = fromHistory
                .Select(h => new SuggestionCandidate(h.Url, h.Title, h.VisitCount, h.VisitedAtUtc, IsBookmark: false))
                .Concat(_bookmarks.All
                    .Where(b => !b.IsFolder
                                && (b.Url!.Contains(query, StringComparison.OrdinalIgnoreCase) || b.Title.Contains(query, StringComparison.OrdinalIgnoreCase)))
                    .Select(b => new SuggestionCandidate(b.Url!, b.Title, 0, null, IsBookmark: true)));

            var ranked = SuggestionRanker.Rank(query, candidates, DateTime.UtcNow);

            Items.Clear();
            foreach (var suggestion in ranked)
            {
                Items.Add(new SuggestionItem(suggestion.Url, suggestion.Title, suggestion.IsBookmark));
            }

            SelectedIndex = -1;
            IsOpen = Items.Count > 0;
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke replaced this query.
        }
        catch (Exception ex)
        {
            _log.Debug($"Suggestions failed: {ex.GetType().Name}");
            Close();
        }
    }

    /// <summary>Arrow keys: moves the highlight, wrapping through "none" (-1). Returns false when the list is closed.</summary>
    public bool Move(int delta)
    {
        if (!IsOpen || Items.Count == 0)
        {
            return false;
        }

        var next = SelectedIndex + delta;
        if (next < -1)
        {
            next = Items.Count - 1;
        }
        else if (next >= Items.Count)
        {
            next = -1;
        }

        SelectedIndex = next;
        return true;
    }

    public void Close()
    {
        _pending?.Cancel();
        IsOpen = false;
        SelectedIndex = -1;
        Items.Clear();
    }

    public void Dispose() => _pending?.Cancel();
}
