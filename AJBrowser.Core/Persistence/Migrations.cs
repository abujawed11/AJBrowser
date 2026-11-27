using Microsoft.Data.Sqlite;

namespace AJBrowser.Core.Persistence;

/// <summary>
/// Forward-only schema migrations. The database's <c>PRAGMA user_version</c> is the number of migrations applied.
/// <para>
/// <b>Rules:</b> never edit or reorder a migration that has shipped; add a new one at the end instead.
/// Each runs in its own transaction, so a failure leaves the database at the previous version.
/// </para>
/// </summary>
public static class Migrations
{
    /// <summary>Migration N (1-based) upgrades the schema from version N-1 to N.</summary>
    internal static readonly string[] Scripts =
    [
        // ---- v1 (Phase 3): history, bookmarks, saved session.
        // Downloads (Phase 4) and per-site settings (Phase 8) get their own migrations when they are built.
        """
        CREATE TABLE history (
            id          INTEGER PRIMARY KEY,
            url         TEXT    NOT NULL UNIQUE,
            title       TEXT    NOT NULL DEFAULT '',
            visited_at  INTEGER NOT NULL,            -- last visit, Unix time in milliseconds (UTC)
            visit_count INTEGER NOT NULL DEFAULT 1
        );
        CREATE INDEX ix_history_visited_at ON history (visited_at DESC);

        CREATE TABLE bookmarks (
            id         INTEGER PRIMARY KEY,
            parent_id  INTEGER NULL REFERENCES bookmarks (id) ON DELETE CASCADE, -- NULL = top level (bookmarks bar)
            title      TEXT    NOT NULL,
            url        TEXT    NULL,                 -- NULL = folder
            position   INTEGER NOT NULL,
            created_at INTEGER NOT NULL
        );
        CREATE INDEX ix_bookmarks_parent ON bookmarks (parent_id, position);
        CREATE INDEX ix_bookmarks_url ON bookmarks (url);

        CREATE TABLE session_tabs (
            window_id INTEGER NOT NULL,
            position  INTEGER NOT NULL,
            url       TEXT    NOT NULL,
            title     TEXT    NOT NULL DEFAULT '',
            is_active INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (window_id, position)
        );
        """,

        // ---- v2 (Phase 3 follow-up): one icon per site, so History, Bookmarks and suggestions can show it.
        """
        CREATE TABLE favicons (
            host       TEXT    PRIMARY KEY,          -- lower-case host name, for example "www.google.com"
            png        BLOB    NOT NULL,             -- the icon as PNG, as WebView2 delivered it
            updated_at INTEGER NOT NULL              -- Unix time in milliseconds (UTC)
        );
        """,

        // ---- v3 (Phase 4): downloads, browser- and app-managed, so the list and its states survive a restart.
        """
        CREATE TABLE downloads (
            id             INTEGER PRIMARY KEY,
            kind           TEXT    NOT NULL,         -- 'Browser' or 'App'
            state          TEXT    NOT NULL,         -- DownloadState name
            source_url     TEXT    NOT NULL,         -- the real address (for retry); never logged
            page_url       TEXT    NULL,
            file_path      TEXT    NOT NULL,
            total_bytes    INTEGER NULL,
            received_bytes INTEGER NOT NULL DEFAULT 0,
            started_at     INTEGER NOT NULL,         -- Unix time in milliseconds (UTC)
            completed_at   INTEGER NULL,
            error          TEXT    NULL,
            accepts_ranges INTEGER NOT NULL DEFAULT 0,
            etag           TEXT    NULL,
            last_modified  TEXT    NULL
        );
        CREATE INDEX ix_downloads_started_at ON downloads (started_at DESC);
        """,

        // ---- v4 (Phase 6): an app download can be an HLS stream (its source_url is then the media playlist).
        """
        ALTER TABLE downloads ADD COLUMN source_type TEXT NOT NULL DEFAULT 'File';  -- DownloadSource name
        """,

        // ---- v5 (Phase 6c): an HLS stream's separate sound track, merged into the result with FFmpeg.
        """
        ALTER TABLE downloads ADD COLUMN audio_url TEXT NULL;
        """,

        // ---- v6 (Phase 7b): a DASH stream's chosen picture and sound tracks (Representation ids; source_url = the manifest).
        """
        ALTER TABLE downloads ADD COLUMN video_track TEXT NULL;
        ALTER TABLE downloads ADD COLUMN audio_track TEXT NULL;
        """,
    ];

    /// <summary>The schema version this build of AJBrowser expects.</summary>
    public static int LatestVersion => Scripts.Length;

    /// <summary>Applies every migration the database hasn't had yet. Returns the resulting version.</summary>
    public static int Apply(SqliteConnection connection) => Apply(connection, Scripts);

    internal static int Apply(SqliteConnection connection, IReadOnlyList<string> scripts)
    {
        var version = GetVersion(connection);
        if (version > scripts.Count)
        {
            // A newer AJBrowser created this database. Don't touch it; old code may still read what it knows.
            return version;
        }

        for (var next = version + 1; next <= scripts.Count; next++)
        {
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = scripts[next - 1];
                command.ExecuteNonQuery();
            }

            using (var setVersion = connection.CreateCommand())
            {
                setVersion.Transaction = transaction;
                setVersion.CommandText = $"PRAGMA user_version = {next};"; // PRAGMA can't take parameters; next is an int
                setVersion.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        return Math.Max(version, scripts.Count);
    }

    public static int GetVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
