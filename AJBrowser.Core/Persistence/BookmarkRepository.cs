namespace AJBrowser.Core.Persistence;

/// <summary>A bookmark, or a folder when <see cref="Url"/> is null. <see cref="ParentId"/> null = top level (the bookmarks bar).</summary>
public sealed record Bookmark(long Id, long? ParentId, string Title, string? Url, int Position, DateTime CreatedAtUtc)
{
    public bool IsFolder => Url is null;
}

/// <summary>
/// Bookmarks and folders. The app keeps all of them in memory (there are rarely more than a few thousand) and
/// writes changes through this repository on a background thread.
/// </summary>
public sealed class BookmarkRepository
{
    public const int MaxTitleLength = 1_000;

    private readonly Database _database;

    public BookmarkRepository(Database database) => _database = database;

    /// <summary>Everything, ordered by parent then position.</summary>
    public Task<IReadOnlyList<Bookmark>> GetAllAsync() => Task.Run<IReadOnlyList<Bookmark>>(() =>
    {
        using var connection = _database.Open();
        using var command = Sql.Command(connection,
            "SELECT id, parent_id, title, url, position, created_at FROM bookmarks ORDER BY parent_id, position, id;");
        var list = new List<Bookmark>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Bookmark(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetInt64(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4),
                Sql.FromUnixMs(reader.GetInt64(5))));
        }

        return list;
    });

    /// <summary>Adds a bookmark (or a folder when <paramref name="url"/> is null) at the end of <paramref name="parentId"/>.</summary>
    public Task<Bookmark> AddAsync(long? parentId, string title, string? url, DateTime createdAtUtc) => Task.Run(() =>
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        using var next = Sql.Command(connection, "SELECT COALESCE(MAX(position) + 1, 0) FROM bookmarks WHERE parent_id IS $parent;", ("$parent", parentId));
        next.Transaction = transaction;
        var position = Convert.ToInt32(next.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);

        var cleanTitle = Sql.Truncate(title, MaxTitleLength);
        using var insert = Sql.Command(connection,
            "INSERT INTO bookmarks (parent_id, title, url, position, created_at) VALUES ($parent, $title, $url, $position, $created) RETURNING id;",
            ("$parent", parentId), ("$title", cleanTitle), ("$url", url), ("$position", position), ("$created", Sql.ToUnixMs(createdAtUtc)));
        insert.Transaction = transaction;
        var id = Convert.ToInt64(insert.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);

        transaction.Commit();
        return new Bookmark(id, parentId, cleanTitle, url, position, createdAtUtc);
    });

    /// <summary>Renames and/or changes the URL. Moving to another folder puts it at the end of that folder.</summary>
    public Task<Bookmark> UpdateAsync(Bookmark bookmark, string title, string? url, long? parentId) => Task.Run(() =>
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        var position = bookmark.Position;
        if (parentId != bookmark.ParentId)
        {
            using var next = Sql.Command(connection, "SELECT COALESCE(MAX(position) + 1, 0) FROM bookmarks WHERE parent_id IS $parent;", ("$parent", parentId));
            next.Transaction = transaction;
            position = Convert.ToInt32(next.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        var cleanTitle = Sql.Truncate(title, MaxTitleLength);
        using var update = Sql.Command(connection,
            "UPDATE bookmarks SET title = $title, url = $url, parent_id = $parent, position = $position WHERE id = $id;",
            ("$id", bookmark.Id), ("$title", cleanTitle), ("$url", bookmark.IsFolder ? null : url), ("$parent", parentId), ("$position", position));
        update.Transaction = transaction;
        update.ExecuteNonQuery();

        transaction.Commit();
        return bookmark with { Title = cleanTitle, Url = bookmark.IsFolder ? null : url, ParentId = parentId, Position = position };
    });

    /// <summary>Deletes a bookmark, or a folder with everything inside it (foreign-key cascade).</summary>
    public Task DeleteAsync(long id) => Task.Run(() =>
    {
        using var connection = _database.Open();
        using var command = Sql.Command(connection, "DELETE FROM bookmarks WHERE id = $id;", ("$id", id));
        command.ExecuteNonQuery();
    });
}
