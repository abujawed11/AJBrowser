namespace AJBrowser.Core.Persistence;

/// <summary>One open tab, saved so the session can be restored on the next start.</summary>
public sealed record SessionTab(string Url, string Title, bool IsActive);

/// <summary>The open tabs of the (single) window. Saving replaces the whole list in one transaction.</summary>
public sealed class SessionRepository
{
    private const int WindowId = 1; // one window for now; multi-window is deferred (plan §9)

    private readonly Database _database;

    public SessionRepository(Database database) => _database = database;

    public Task SaveAsync(IReadOnlyList<SessionTab> tabs) => Task.Run(() => Save(tabs));

    /// <summary>Synchronous save, for app exit (there is no later moment to finish an async one).</summary>
    public void Save(IReadOnlyList<SessionTab> tabs)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        using (var clear = Sql.Command(connection, "DELETE FROM session_tabs WHERE window_id = $window;", ("$window", WindowId)))
        {
            clear.Transaction = transaction;
            clear.ExecuteNonQuery();
        }

        for (var i = 0; i < tabs.Count; i++)
        {
            using var insert = Sql.Command(connection,
                "INSERT INTO session_tabs (window_id, position, url, title, is_active) VALUES ($window, $position, $url, $title, $active);",
                ("$window", WindowId), ("$position", i), ("$url", tabs[i].Url),
                ("$title", Sql.Truncate(tabs[i].Title, HistoryRepository.MaxTitleLength)), ("$active", tabs[i].IsActive ? 1 : 0));
            insert.Transaction = transaction;
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public Task<IReadOnlyList<SessionTab>> LoadAsync() => Task.Run<IReadOnlyList<SessionTab>>(() =>
    {
        using var connection = _database.Open();
        using var command = Sql.Command(connection,
            "SELECT url, title, is_active FROM session_tabs WHERE window_id = $window ORDER BY position;", ("$window", WindowId));
        var list = new List<SessionTab>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new SessionTab(reader.GetString(0), reader.GetString(1), reader.GetInt64(2) != 0));
        }

        return list;
    });
}
