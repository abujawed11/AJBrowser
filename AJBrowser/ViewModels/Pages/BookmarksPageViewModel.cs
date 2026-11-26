using System.Collections.ObjectModel;
using AJBrowser.Core.Navigation;
using AJBrowser.Core.Persistence;
using AJBrowser.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AJBrowser.ViewModels.Pages;

/// <summary>One row on the Bookmarks page: a bookmark or a folder.</summary>
public sealed class BookmarkItemViewModel(Bookmark bookmark, string? folderPath = null)
{
    public Bookmark Bookmark { get; } = bookmark;

    public bool IsFolder => Bookmark.IsFolder;

    public string Title => Bookmark.Title;

    /// <summary>Second line: the address, or the folder it's in when searching.</summary>
    public string Detail => IsFolder ? "Folder" : folderPath is null ? UrlDisplay.ForAddressBar(Bookmark.Url) : $"{UrlDisplay.ForAddressBar(Bookmark.Url)}  ·  {folderPath}";

    public string Glyph => IsFolder ? "" : "";
}

/// <summary>
/// ajbrowser://bookmarks: browse folders, search, open, and edit the selected item in the side panel
/// (name, address, folder) or delete it. "New folder" creates one in the current folder.
/// </summary>
public sealed partial class BookmarksPageViewModel : ObservableObject, IInternalPage
{
    private readonly BookmarkService _bookmarks;
    private readonly IPageHost _host;

    public BookmarksPageViewModel(BookmarkService bookmarks, IPageHost host)
    {
        _bookmarks = bookmarks;
        _host = host;
        _bookmarks.Changed += OnBookmarksChanged;
        Refresh();
    }

    public InternalPage Kind => InternalPage.Bookmarks;

    public string Title => InternalPages.TitleOf(InternalPage.Bookmarks);

    public ObservableCollection<BookmarkItemViewModel> Items { get; } = [];

    /// <summary>The folder being shown (null = the bookmarks bar, the top level).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoUp))]
    [NotifyCanExecuteChangedFor(nameof(GoUpCommand))]
    public partial long? CurrentFolderId { get; set; }

    [ObservableProperty]
    public partial string FolderPath { get; set; } = BookmarkService.BarFolderName;

    public bool CanGoUp => CurrentFolderId is not null && !IsSearching;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearching), nameof(CanGoUp))]
    [NotifyCanExecuteChangedFor(nameof(GoUpCommand), nameof(NewFolderCommand))]
    public partial string SearchText { get; set; } = string.Empty;

    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial string EmptyText { get; set; } = string.Empty;

    // ---- side panel (selected item)

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(IsSelectedBookmark))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DeleteSelectedCommand))]
    public partial BookmarkItemViewModel? SelectedItem { get; set; }

    public bool HasSelection => SelectedItem is not null;

    /// <summary>The selection is a bookmark (has an address to edit), not a folder.</summary>
    public bool IsSelectedBookmark => SelectedItem is { IsFolder: false };

    [ObservableProperty]
    public partial string EditName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<BookmarkFolderChoice> FolderChoices { get; set; } = [];

    [ObservableProperty]
    public partial BookmarkFolderChoice? EditFolder { get; set; }

    [ObservableProperty]
    public partial string EditError { get; set; } = string.Empty;

    public void Activate() => Refresh();

    partial void OnSearchTextChanged(string value) => Refresh();

    partial void OnSelectedItemChanged(BookmarkItemViewModel? value)
    {
        EditError = string.Empty;
        if (value is null)
        {
            return;
        }

        EditName = value.Bookmark.Title;
        EditUrl = value.Bookmark.Url ?? string.Empty;
        FolderChoices = _bookmarks.FolderChoices(value.IsFolder ? value.Bookmark : null);
        EditFolder = FolderChoices.FirstOrDefault(f => f.Id == value.Bookmark.ParentId) ?? FolderChoices[0];
    }

    /// <summary>Double-click or Enter: a folder opens, a bookmark opens in this tab.</summary>
    [RelayCommand]
    private void OpenItem(BookmarkItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.IsFolder)
        {
            SearchText = string.Empty;
            CurrentFolderId = item.Bookmark.Id;
            Refresh();
        }
        else if (Uri.TryCreate(item.Bookmark.Url, UriKind.Absolute, out var uri))
        {
            _host.Open(uri, OpenTarget.CurrentTab);
        }
    }

    /// <summary>Middle-click on a bookmark: open in a background tab.</summary>
    public void OpenInNewTab(BookmarkItemViewModel? item)
    {
        if (item is { IsFolder: false } && Uri.TryCreate(item.Bookmark.Url, UriKind.Absolute, out var uri))
        {
            _host.Open(uri, OpenTarget.NewBackgroundTab);
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private void GoUp()
    {
        CurrentFolderId = CurrentFolderId is { } id ? _bookmarks.Find(id)?.ParentId : null;
        Refresh();
    }

    private bool CanCreateFolder() => !IsSearching;

    [RelayCommand(CanExecute = nameof(CanCreateFolder))]
    private async Task NewFolder()
    {
        var folder = await _bookmarks.AddAsync(CurrentFolderId, "New folder", url: null);
        if (folder is not null)
        {
            SelectedItem = Items.FirstOrDefault(i => i.Bookmark.Id == folder.Id);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Save()
    {
        if (SelectedItem is not { } item || _bookmarks.Find(item.Bookmark.Id) is not { } current)
        {
            return;
        }

        string? url = null;
        if (!current.IsFolder)
        {
            if (!Uri.TryCreate(EditUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is "javascript" or "vbscript")
            {
                EditError = "Enter a full address, for example https://example.com/";
                return;
            }

            url = uri.AbsoluteUri;
        }

        EditError = string.Empty;
        var updated = await _bookmarks.UpdateAsync(current, EditName, url, EditFolder?.Id);
        if (updated is not null)
        {
            SelectedItem = Items.FirstOrDefault(i => i.Bookmark.Id == updated.Id); // null if it moved to another folder
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteSelected()
    {
        if (SelectedItem is { } item)
        {
            SelectedItem = null;
            await _bookmarks.DeleteAsync(item.Bookmark);
        }
    }

    private void Refresh()
    {
        var selectedId = SelectedItem?.Bookmark.Id;

        // The folder being shown may have been deleted meanwhile: fall back to the top level.
        if (CurrentFolderId is { } folderId && _bookmarks.Find(folderId) is null)
        {
            CurrentFolderId = null;
        }

        Items.Clear();
        if (IsSearching)
        {
            var text = SearchText.Trim();
            foreach (var bookmark in _bookmarks.All
                         .Where(b => b.Title.Contains(text, StringComparison.OrdinalIgnoreCase)
                                     || (b.Url?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false))
                         .OrderBy(b => b.Title, StringComparer.CurrentCultureIgnoreCase))
            {
                Items.Add(new BookmarkItemViewModel(bookmark, _bookmarks.FolderName(bookmark.ParentId)));
            }

            FolderPath = $"Search results for “{text}”";
            EmptyText = "No bookmarks match your search.";
        }
        else
        {
            foreach (var bookmark in _bookmarks.ChildrenOf(CurrentFolderId))
            {
                Items.Add(new BookmarkItemViewModel(bookmark));
            }

            FolderPath = _bookmarks.FolderName(CurrentFolderId);
            EmptyText = CurrentFolderId is null
                ? "No bookmarks yet. Click the star in the address bar (Ctrl+D) to bookmark a page."
                : "This folder is empty.";
        }

        IsEmpty = Items.Count == 0;
        SelectedItem = selectedId is null ? null : Items.FirstOrDefault(i => i.Bookmark.Id == selectedId);
        OnPropertyChanged(nameof(CanGoUp));
        GoUpCommand.NotifyCanExecuteChanged();
    }

    private void OnBookmarksChanged(object? sender, EventArgs e) => Refresh();

    public void Dispose() => _bookmarks.Changed -= OnBookmarksChanged;
}
