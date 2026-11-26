using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using AJBrowser.Core.Navigation;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AJBrowser.ViewModels.Pages;

/// <summary>
/// ajbrowser://downloads (Ctrl+J): every download with search, per-item actions and "Clear finished".
/// The rows are the shared <see cref="DownloadsViewModel.Items"/>, seen through this page's own filtered view
/// (like a filtered array in React that doesn't copy the items).
/// </summary>
public sealed partial class DownloadsPageViewModel : ObservableObject, IInternalPage
{
    private readonly ListCollectionView _view;

    public DownloadsPageViewModel(DownloadsViewModel downloads)
    {
        Downloads = downloads;
        _view = new ListCollectionView(downloads.Items) { Filter = Matches };
        downloads.Items.CollectionChanged += OnItemsChanged;
        downloads.RefreshFiles();
    }

    public InternalPage Kind => InternalPage.Downloads;

    public string Title => InternalPages.TitleOf(InternalPage.Downloads);

    /// <summary>The shared list (also for the page's "Open folder" and "Clear finished" commands).</summary>
    public DownloadsViewModel Downloads { get; }

    public ICollectionView Items => _view;

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    public bool IsEmpty => _view.IsEmpty;

    public string EmptyText => string.IsNullOrWhiteSpace(SearchText) ? "Files you download appear here." : "No downloads match your search.";

    public void Activate() => Downloads.RefreshFiles();

    partial void OnSearchTextChanged(string value)
    {
        _view.Refresh();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
    }

    private bool Matches(object row)
    {
        var text = SearchText.Trim();
        return text.Length == 0
               || row is DownloadItemViewModel item
               && (item.FileName.Contains(text, StringComparison.OrdinalIgnoreCase)
                   || item.DisplayUrl.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => OnPropertyChanged(nameof(IsEmpty));

    public void Dispose()
    {
        Downloads.Items.CollectionChanged -= OnItemsChanged;
        _view.DetachFromSourceCollection();
    }
}
