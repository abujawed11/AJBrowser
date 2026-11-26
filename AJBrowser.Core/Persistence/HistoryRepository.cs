namespace AJBrowser.Core.Persistence;

/// <summary>One history row: a URL with its latest visit (the schema keeps one row per URL, plan Phase 3).</summary>
public sealed record HistoryEntry(long Id, string Url, string Title, DateTime VisitedAtUtc, int VisitCount);

/// <summary>
/// Browsing history. Every method runs its SQL on a background thread, so callers on the UI thread just await.
/// </summary>
public sealed class HistoryRepository
{
    /// <summary>Revisiting a URL within this time (reloads, redirects back, SPA hops) doesn't count as another visit.</summary>
    public static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(30);

    public const int MaxTitleLength = 1_000;

    /// <summary>Longer URLs aren't recorded (they are usually data blobs or tracking junk).</summary>
    public const int MaxUrlLength = 8_192;

    private readonly Database _database;

    public HistoryRepository(Database database) => _database = database;

    /// <summary>Records a visit: inserts the URL, or updates its time (and count, outside <see cref="MergeWindow"/>).</summary>
    public Task RecordVisitAsync(string url, string? title, DateTime visitedAtUtc) => Task.Run(() =>
    {
        if (url.Length > MaxUrlLength)
        {
            return;
        }

        using var connection = _database.Open();
        using var command = Sql.Command(connection,
            """
            INSERT INTO history (url, title, visited_at, visit_count) VALUES ($url, $title, $now, 1)
            ON CONFLICT (url) DO UPDATE SET
                title       = CASE WHEN excluded.title <> '' THEN excluded.title ELSE history.title END,
                visit_count = history.visit_count + CASE WHEN $now - history.visited_at >= $window THEN 1 ELSE 0 END,
                visited_at  = excluded.visited_at;
            """,
            ("$url", url),
            ("$title", Sql.Truncate(title, MaxTitleLength)),
            ("$now", Sql.ToUnixMs(visitedAtUtc)),
            ("$window", (long)MergeWindow.TotalMilliseconds));
        command.ExecuteNonQuery();
    });

    /// <summary>The page's title arrived after the visit was recorded.</summary>
    public Task UpdateTitleAsync(string url, string title) => Task.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        using var connection = _database.Open();
        using var command = Sql.Command(connection, "UPDATE history SET title = $title WHERE url = $url;",
            ("$url", url), ("$title", Sql.Truncate(title, MaxTitleLength)));
        command.ExecuteNonQuery();
    });

    /// <summary>Newest first. An empty <paramref name="query"/> lists everything; otherwise URL or title must contain it.</summary>
    public Task<IReadOnlyList<HistoryEntry>> SearchAsync(string? query, int limit, int offset = 0) => Task.Run<IReadOnlyList<HistoryEntry>>(() =>
    {
        var text = query?.Trim() ?? string.Empty;
        using var connection = _database.Open();
        using var command = Sql.Command(connection,
            """
            SELECT id, url, title, visited_at, visit_count FROM history
            WHERE $q = '' OR url LIKE $pattern ESCAPE '\' OR title LIKE $pattern ESCAPE '\'
            ORDER BY visited_at DESC, id DESC
            LIMIT $limit OFFSET $offset;
            """,
            ("$q", text), ("$pattern", Sql.ContainsPattern(text)), ("$limit", limit), ("$offset", offset));
        return Read(command);
    });

    /// <summary>Candidates for address-bar suggestions: rows whose URL or title contains the text, most visited first.</summary>
    public Task<IReadOnlyList<HistoryEntry>> FindSuggestionCandidatesAsync(string text, int limit) => Task.Run<IReadOnlyList<HistoryEntry>>(() =>
    {
        using var connection = _database.Open();
        using var command = Sql.Command(connection,
            """
            SELECT id, url, title, visited_at, visit_count FROM history
            WHERE url LIKE $pattern ESCAPE '\' OR title LIKE $pattern ESCAPE '\'
            ORDER BY visit_count DESC, visited_at DESC
            LIMIT $limit;
            """,
            ("$pattern", Sql.ContainsPattern(text.Trim())), ("$limit", limit));
        return Read(command);
    });

    public Task DeleteAsync(long id) => Task.Run(() =>
    {
        using var connection = _database.Open();
        using var command = Sql.Command(connection, "DELETE FROM history WHERE id = $id;", ("$id", id));
        command.ExecuteNonQuery();
    });

    /// <summary>Deletes entries last visited in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>). Returns how many.</summary>
    public Task<int> DeleteRangeAsync(DateTime fromUtc, DateTime toUtc) => Task.Run(() =>
    {
        using var connection = _database.Open();
        using var command = Sql.Command(connection, "DELETE FROM history WHERE visited_at >= $from AND visited_at < $to;",
            ("$from", Sql.ToUnixMs(fromUtc)), ("$to", Sql.ToUnixMs(toUtc)));
        return command.ExecuteNonQuery();
    });

    public Task<int> CountAsync() => Task.Run(() =>
    {
        using var connection = _database.Open();
        using var command = Sql.Command(connection, "SELECT COUNT(*) FROM history;");
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    });

    private static List<HistoryEntry> Read(Microsoft.Data.Sqlite.SqliteCommand command)
    {
        var list = new List<HistoryEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new HistoryEntry(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), Sql.FromUnixMs(reader.GetInt64(3)), reader.GetInt32(4)));
        }

        return list;
    }
}
