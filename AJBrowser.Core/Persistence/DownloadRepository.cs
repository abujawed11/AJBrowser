using AJBrowser.Core.Downloads;
using Microsoft.Data.Sqlite;

namespace AJBrowser.Core.Persistence;

/// <summary>The downloads list. SQL runs on a background thread; callers await.</summary>
public sealed class DownloadRepository
{
    private const string Columns =
        "id, kind, state, source_url, page_url, file_path, total_bytes, received_bytes, started_at, completed_at, error, accepts_ranges, etag, last_modified";

    private readonly Database _database;

    public DownloadRepository(Database database) => _database = database;

    /// <summary>Saves a new download and returns it with its id.</summary>
    public Task<DownloadItem> AddAsync(DownloadItem item) => Task.Run(() =>
    {
        using var connection = _database.Open();
        using var command = Sql.Command(connection,
            """
            INSERT INTO downloads (kind, state, source_url, page_url, file_path, total_bytes, received_bytes, started_at, completed_at, error, accepts_ranges, etag, last_modified)
            VALUES ($kind, $state, $source, $page, $path, $total, $received, $started, $completed, $error, $ranges, $etag, $modified)
            RETURNING id;
            """,
            Parameters(item));
        var id = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        return item with { Id = id };
    });

    public Task UpdateAsync(DownloadItem item) => Task.Run(() =>
    {
        using var connection = _database.Open();
        using var command = Sql.Command(connection,
            """
            UPDATE downloads SET kind = $kind, state = $state, source_url = $source, page_url = $page, file_path = $path,
                total_bytes = $total, received_bytes = $received, started_at = $started, completed_at = $completed, error = $error,
                accepts_ranges = $ranges, etag = $etag, last_modified = $modified
            WHERE id = $id;
            """,
            [.. Parameters(item), ("$id", item.Id)]);
        command.ExecuteNonQuery();
    });

    /// <summary>Newest first.</summary>
    public Task<IReadOnlyList<DownloadItem>> GetRecentAsync(int limit) => Task.Run<IReadOnlyList<DownloadItem>>(() =>
    {
        using var connection = _database.Open();
        using var command = Sql.Command(connection, $"SELECT {Columns} FROM downloads ORDER BY started_at DESC, id DESC LIMIT $limit;", ("$limit", limit));
        var list = new List<DownloadItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(Read(reader));
        }

        return list;
    });

    public Task DeleteAsync(long id) => Task.Run(() =>
    {
        using var connection = _database.Open();
        using var command = Sql.Command(connection, "DELETE FROM downloads WHERE id = $id;", ("$id", id));
        command.ExecuteNonQuery();
    });

    /// <summary>Removes finished entries (completed, failed, cancelled, interrupted) from the list. Files are not touched.</summary>
    public Task<int> DeleteFinishedAsync() => Task.Run(() =>
    {
        using var connection = _database.Open();
        using var command = Sql.Command(connection, "DELETE FROM downloads WHERE state IN ('Completed', 'Failed', 'Cancelled', 'Interrupted');");
        return command.ExecuteNonQuery();
    });

    /// <summary>
    /// At startup, nothing is running yet: browser downloads that were active died with the browser process
    /// (Interrupted); app downloads that were active become Paused, ready to resume from their partial file.
    /// Returns how many entries changed.
    /// </summary>
    public Task<int> MarkStaleAsync() => Task.Run(() =>
    {
        using var connection = _database.Open();
        using var browser = Sql.Command(connection,
            "UPDATE downloads SET state = 'Interrupted', error = COALESCE(error, 'AJBrowser was closed during the download.') WHERE kind = 'Browser' AND state IN ('Queued', 'Downloading', 'Paused');");
        using var app = Sql.Command(connection,
            "UPDATE downloads SET state = 'Paused' WHERE kind = 'App' AND state IN ('Queued', 'Downloading');");
        return browser.ExecuteNonQuery() + app.ExecuteNonQuery();
    });

    private static (string, object?)[] Parameters(DownloadItem item) =>
    [
        ("$kind", item.Kind.ToString()),
        ("$state", item.State.ToString()),
        ("$source", item.SourceUrl),
        ("$page", item.PageUrl),
        ("$path", item.FilePath),
        ("$total", item.TotalBytes),
        ("$received", item.ReceivedBytes),
        ("$started", Sql.ToUnixMs(item.StartedAtUtc)),
        ("$completed", item.CompletedAtUtc is { } completed ? Sql.ToUnixMs(completed) : null),
        ("$error", item.Error),
        ("$ranges", item.Resume?.AcceptsRanges == true ? 1 : 0),
        ("$etag", item.Resume?.ETag),
        ("$modified", item.Resume?.LastModified),
    ];

    private static DownloadItem Read(SqliteDataReader r)
    {
        var acceptsRanges = r.GetInt64(11) != 0;
        var etag = r.IsDBNull(12) ? null : r.GetString(12);
        var lastModified = r.IsDBNull(13) ? null : r.GetString(13);
        return new DownloadItem
        {
            Id = r.GetInt64(0),
            Kind = Enum.TryParse<DownloadKind>(r.GetString(1), out var kind) ? kind : DownloadKind.Browser,
            State = Enum.TryParse<DownloadState>(r.GetString(2), out var state) ? state : DownloadState.Interrupted,
            SourceUrl = r.GetString(3),
            PageUrl = r.IsDBNull(4) ? null : r.GetString(4),
            FilePath = r.GetString(5),
            TotalBytes = r.IsDBNull(6) ? null : r.GetInt64(6),
            ReceivedBytes = r.GetInt64(7),
            StartedAtUtc = Sql.FromUnixMs(r.GetInt64(8)),
            CompletedAtUtc = r.IsDBNull(9) ? null : Sql.FromUnixMs(r.GetInt64(9)),
            Error = r.IsDBNull(10) ? null : r.GetString(10),
            Resume = acceptsRanges || etag is not null || lastModified is not null ? new ResumeInfo(acceptsRanges, etag, lastModified) : null,
        };
    }
}
