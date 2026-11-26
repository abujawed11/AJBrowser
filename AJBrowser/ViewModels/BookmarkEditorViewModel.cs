using AJBrowser.Core.Persistence;
using AJBrowser.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AJBrowser.ViewModels;

/// <summary>
/// The small panel under the star button (Ctrl+D): rename the bookmark, pick its folder, or remove it.
/// Changes are saved when the panel closes, like Chrome.
/// </summary>
public sealed partial class BookmarkEditorViewModel : ObservableObject
{
    private readonly BookmarkService _bookmarks;
    private readonly Bookmark _bookmark;
    private bool _removed;

    public BookmarkEditorViewModel(BookmarkService bookmarks, Bookmark bookmark, bool isNew)
    {
        _bookmarks = bookmarks;
        _bookmark = bookmark;
        Heading = isNew ? "Bookmark added" : "Edit bookmark";
        Name = bookmark.Title;
        Folders = bookmarks.FolderChoices();
        SelectedFolder = Folders.FirstOrDefault(f => f.Id == bookmark.ParentId) ?? Folders[0];
    }

    public string Heading { get; }

    public IReadOnlyList<BookmarkFolderChoice> Folders { get; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial BookmarkFolderChoice SelectedFolder { get; set; }

    /// <summary>The "Remove" button. The view closes the panel afterwards.</summary>
    [RelayCommand]
    private async Task Remove()
    {
        _removed = true;
        await _bookmarks.DeleteAsync(_bookmark);
    }

    /// <summary>Saves a changed name or folder. Called when the panel closes.</summary>
    public async Task CommitAsync()
    {
        if (_removed || _bookmarks.Find(_bookmark.Id) is not { } current)
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(Name) ? current.Title : Name.Trim();
        if (name != current.Title || SelectedFolder.Id != current.ParentId)
        {
            await _bookmarks.UpdateAsync(current, name, current.Url, SelectedFolder.Id);
        }
    }
}
