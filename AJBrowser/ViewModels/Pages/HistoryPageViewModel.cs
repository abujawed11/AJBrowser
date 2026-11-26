using System.Collections.ObjectModel;
using System.Globalization;
using AJBrowser.Browser;
using AJBrowser.Core.Logging;
using AJBrowser.Core.Navigation;
using AJBrowser.Core.Persistence;
using AJBrowser.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AJBrowser.ViewModels.Pages;

/// <summary>One history row on the page.</summary>
public sealed class HistoryItemViewModel(HistoryEntry entry, System.Windows.Media.ImageSource? icon)
{
    public HistoryEntry Entry { get; } = entry;

    /// <summary>The site's icon, or null (then a globe is shown).</summary>
    public System.Windows.Media.ImageSource? Icon { get; } = icon;

    public string Title => string.IsNullOrWhiteSpace(Entry.Title) ? UrlDisplay.ForAddressBar(Entry.Url) : Entry.Title;

    public string Host => Uri.TryCreate(Entry.Url, UriKind.Absolute, out var uri) ? uri.Host : Entry.Url;

    public string Time => Entry.VisitedAtUtc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    public string ToolTip => Entry.VisitCount > 1 ? $"{Entry.Url}\nVisited {Entry.VisitCount} times" : Entry.Url;
}

/// <summary>A day heading with its entries.</summary>
public sealed class HistoryDayGroup(string label)
{
    public string Label { get; } = label;

    public ObservableCollection<HistoryItemViewModel> Items { get; } = [];
}

/// <summary>A choice in the "Clear browsing data" time-range box.</summary>
public sealed record ClearRangeOption(ClearRange Range, string Label);

/// <summary>
/// ajbrowser://history: search, entries grouped by day (newest first, loaded 200 at a time), delete one entry,
/// and "Clear browsing data" for a time range, optionally with cookies, site data and cache.
/// </summary>
public sealed partial class HistoryPageViewModel : ObservableObject, IInternalPage
{
    private const int PageSize = 200;
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(250);

    private readonly HistoryService _history;
    private readonly FaviconService _favicons;
    private readonly BrowserEnvironment _browser;
    private readonly IPageHost _host;
    private readonly AppLogger _log;
    private CancellationTokenSource? _searchDelay;
    private int _loadedCount;
    private int _loadVersion;

    public HistoryPageViewModel(HistoryService history, FaviconService favicons, BrowserEnvironment browser, IPageHost host, AppLogger log)
    {
        _history = history;
        _favicons = favicons;
        _browser = browser;
        _host = host;
        _log = log;
        SelectedClearRange = ClearRanges[0];
        _history.Cleared += OnHistoryCleared;
        _ = ReloadAsync();
    }

    public InternalPage Kind => InternalPage.History;

    public string Title => InternalPages.TitleOf(InternalPage.History);

    public ObservableCollection<HistoryDayGroup> Groups { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool HasMore { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial string EmptyText { get; set; } = string.Empty;

    // ---- clear browsing data

    [ObservableProperty]
    public partial bool IsClearPanelOpen { get; set; }

    public IReadOnlyList<ClearRangeOption> ClearRanges { get; } =
    [
        new(ClearRange.LastHour, "Last hour"),
        new(ClearRange.Last24Hours, "Last 24 hours"),
        new(ClearRange.Last7Days, "Last 7 days"),
        new(ClearRange.Last4Weeks, "Last 4 weeks"),
        new(ClearRange.AllTime, "All time"),
    ];

    [ObservableProperty]
    public partial ClearRangeOption SelectedClearRange { get; set; }

    /// <summary>Also clear cookies, site storage and the cache (signs you out of sites).</summary>
    [ObservableProperty]
    public partial bool AlsoClearSiteData { get; set; }

    [ObservableProperty]
    public partial string ClearStatus { get; set; } = string.Empty;

    public void Activate() => _ = ReloadAsync();

    partial void OnSearchTextChanged(string value) => _ = ReloadAfterDelayAsync();

    [RelayCommand]
    private void Open(HistoryItemViewModel? item) => OpenItem(item, OpenTarget.CurrentTab);

    /// <summary>Middle-click: open in a background tab.</summary>
    public void OpenInNewTab(HistoryItemViewModel? item) => OpenItem(item, OpenTarget.NewBackgroundTab);

    [RelayCommand]
    private async Task Delete(HistoryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        await _history.DeleteAsync(item.Entry.Id); // raises Cleared → reload
    }

    [RelayCommand(CanExecute = nameof(HasMore))]
    private async Task LoadMore() => await LoadPageAsync(_loadVersion, append: true);

    [RelayCommand]
    private void ToggleClearPanel()
    {
        IsClearPanelOpen = !IsClearPanelOpen;
        ClearStatus = string.Empty;
    }

    public void OpenClearPanel()
    {
        IsClearPanelOpen = true;
        ClearStatus = string.Empty;
    }

    [RelayCommand]
    private async Task ClearData()
    {
        var from = HistoryRules.RangeStartUtc(SelectedClearRange.Range, DateTime.UtcNow);
        ClearStatus = "Clearing…";
        try
        {
            var deleted = await _history.ClearAsync(from);
            var status = $"Deleted {deleted} history {(deleted == 1 ? "entry" : "entries")}";
            if (AlsoClearSiteData)
            {
                status += await _browser.ClearBrowsingDataAsync(from)
                    ? ", and cleared cookies, site data and cache."
                    : ". Cookies and cache weren't cleared: open any web page first, then try again.";
            }
            else
            {
                status += ".";
            }

            ClearStatus = status;
        }
        catch (Exception ex)
        {
            _log.Error("Clearing browsing data failed", ex);
            ClearStatus = "Something went wrong while clearing. Details are in the log.";
        }
    }

    // ---------------------------------------------------------------- loading

    private async Task ReloadAfterDelayAsync()
    {
        _searchDelay?.Cancel();
        var cts = _searchDelay = new CancellationTokenSource();
        try
        {
            await Task.Delay(SearchDebounce, cts.Token);
            await ReloadAsync();
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke replaced this search.
        }
    }

    private async Task ReloadAsync()
    {
        var version = ++_loadVersion;
        await LoadPageAsync(version, append: false);
    }

    private async Task LoadPageAsync(int version, bool append)
    {
        try
        {
            var offset = append ? _loadedCount : 0;
            var entries = await _history.SearchAsync(SearchText, PageSize + 1, offset);
            if (version != _loadVersion)
            {
                return; // a newer search started meanwhile
            }

            if (!append)
            {
                Groups.Clear();
                _loadedCount = 0;
            }

            var page = entries.Take(PageSize).ToList();
            var today = DateTime.Now;
            foreach (var entry in page)
            {
                var label = HistoryRules.DayLabel(entry.VisitedAtUtc.ToLocalTime(), today, CultureInfo.CurrentCulture);
                var group = Groups.Count > 0 && Groups[^1].Label == label ? Groups[^1] : null;
                if (group is null)
                {
                    group = new HistoryDayGroup(label);
                    Groups.Add(group);
                }

                group.Items.Add(new HistoryItemViewModel(entry, _favicons.Get(entry.Url)));
            }

            _loadedCount += page.Count;
            HasMore = entries.Count > PageSize;
            IsEmpty = _loadedCount == 0;
            EmptyText = string.IsNullOrWhiteSpace(SearchText) ? "Pages you visit will show up here." : "No history matches your search.";
        }
        catch (Exception ex)
        {
            _log.Error("Loading history failed", ex);
        }
    }

    private void OpenItem(HistoryItemViewModel? item, OpenTarget target)
    {
        if (item is not null && Uri.TryCreate(item.Entry.Url, UriKind.Absolute, out var uri))
        {
            _host.Open(uri, target);
        }
    }

    private void OnHistoryCleared(object? sender, EventArgs e) => _ = ReloadAsync();

    public void Dispose()
    {
        _searchDelay?.Cancel();
        _history.Cleared -= OnHistoryCleared;
    }
}
