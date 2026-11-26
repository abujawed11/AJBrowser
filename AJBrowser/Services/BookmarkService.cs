using AJBrowser.Core.Logging;
using AJBrowser.Core.Persistence;

namespace AJBrowser.Services;

/// <summary>A folder a bookmark can be put in, for pickers. <see cref="Id"/> null = the bookmarks bar (top level).</summary>
public sealed record BookmarkFolderChoice(long? Id, string Name);

/// <summary>
/// All bookmarks, kept in memory (loaded once at startup) so the star button and the bookmarks bar never wait for
/// the database. Changes are written to the database first and then applied here, and <see cref="Changed"/> tells
/// every view to refresh. Use it from the UI thread only.
/// </summary>
public sealed class BookmarkService
{
    public const string BarFolderName = "Bookmarks bar";

    private readonly BookmarkRepository _repository;
    private readonly AppLogger _log;
    private readonly List<Bookmark> _all = [];

    public BookmarkService(BookmarkRepository repository, AppLogger log)
    {
        _repository = repository;
        _log = log;
    }

    public IReadOnlyList<Bookmark> All => _all;

    /// <summary>Something was added, changed or deleted.</summary>
    public event EventHandler? Changed;

    public async Task LoadAsync()
    {
        try
        {
            var loaded = await _repository.GetAllAsync();
            _all.Clear();
            _all.AddRange(loaded);
            _log.Info($"Loaded {_all.Count} bookmarks and folders");
        }
        catch (Exception ex)
        {
            _log.Error("Could not load bookmarks", ex);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Bookmark? FindByUrl(string? url) =>
        string.IsNullOrEmpty(url) ? null : _all.FirstOrDefault(b => !b.IsFolder && string.Equals(b.Url, url, StringComparison.Ordinal));

    public Bookmark? Find(long id) => _all.FirstOrDefault(b => b.Id == id);

    public IReadOnlyList<Bookmark> ChildrenOf(long? parentId) =>
        _all.Where(b => b.ParentId == parentId).OrderBy(b => b.Position).ThenBy(b => b.Id).ToList();

    /// <summary>"Bookmarks bar", then every folder as a path ("Work / Docs"). Excludes <paramref name="exceptFolder"/> and its subfolders.</summary>
    public IReadOnlyList<BookmarkFolderChoice> FolderChoices(Bookmark? exceptFolder = null)
    {
        var result = new List<BookmarkFolderChoice> { new(null, BarFolderName) };
        void Walk(long? parentId, string prefix)
        {
            foreach (var folder in ChildrenOf(parentId).Where(b => b.IsFolder))
            {
                if (exceptFolder is not null && folder.Id == exceptFolder.Id)
                {
                    continue;
                }

                var name = prefix.Length == 0 ? folder.Title : $"{prefix} / {folder.Title}";
                result.Add(new BookmarkFolderChoice(folder.Id, name));
                Walk(folder.Id, name);
            }
        }

        Walk(null, string.Empty);
        return result;
    }

    /// <summary>The folder path for display ("Bookmarks bar", "Work / Docs").</summary>
    public string FolderName(long? folderId) =>
        FolderChoices().FirstOrDefault(c => c.Id == folderId)?.Name ?? BarFolderName;

    /// <summary>Adds a bookmark (or a folder when <paramref name="url"/> is null). Returns null if saving failed.</summary>
    public async Task<Bookmark?> AddAsync(long? parentId, string title, string? url)
    {
        try
        {
            var added = await _repository.AddAsync(parentId, CleanTitle(title, url), url, DateTime.UtcNow);
            _all.Add(added);
            Changed?.Invoke(this, EventArgs.Empty);
            return added;
        }
        catch (Exception ex)
        {
            _log.Error("Could not add a bookmark", ex);
            return null;
        }
    }

    public async Task<Bookmark?> UpdateAsync(Bookmark bookmark, string title, string? url, long? parentId)
    {
        if (parentId is { } target && IsSelfOrDescendant(target, bookmark.Id))
        {
            parentId = bookmark.ParentId; // a folder can't go inside itself
        }

        try
        {
            var updated = await _repository.UpdateAsync(bookmark, CleanTitle(title, url ?? bookmark.Url), url, parentId);
            var index = _all.FindIndex(b => b.Id == bookmark.Id);
            if (index >= 0)
            {
                _all[index] = updated;
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return updated;
        }
        catch (Exception ex)
        {
            _log.Error("Could not update a bookmark", ex);
            return null;
        }
    }

    /// <summary>Deletes a bookmark, or a folder with everything in it.</summary>
    public async Task DeleteAsync(Bookmark bookmark)
    {
        try
        {
            await _repository.DeleteAsync(bookmark.Id);
            var doomed = new HashSet<long> { bookmark.Id };
            bool added;
            do
            {
                added = false;
                foreach (var child in _all.Where(b => b.ParentId is { } p && doomed.Contains(p) && !doomed.Contains(b.Id)).ToList())
                {
                    doomed.Add(child.Id);
                    added = true;
                }
            }
            while (added);

            _all.RemoveAll(b => doomed.Contains(b.Id));
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.Error("Could not delete a bookmark", ex);
        }
    }

    private bool IsSelfOrDescendant(long candidateId, long folderId)
    {
        for (long? id = candidateId; id is { } current; id = Find(current)?.ParentId)
        {
            if (current == folderId)
            {
                return true;
            }
        }

        return false;
    }

    private static string CleanTitle(string? title, string? url)
    {
        var text = title?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            return text;
        }

        return url is null ? "New folder" : url;
    }
}
