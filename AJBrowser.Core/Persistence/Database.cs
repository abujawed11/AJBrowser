using Microsoft.Data.Sqlite;

namespace AJBrowser.Core.Persistence;

/// <summary>What happened when the database was opened at startup.</summary>
public sealed record DatabaseOpenResult(int SchemaVersion, bool RecreatedAfterCorruption, string? CorruptBackupPath);

/// <summary>
/// AJBrowser's SQLite database (<c>Data\ajbrowser.db</c>): history, bookmarks and the saved session.
/// <para>
/// Each operation opens its own short-lived connection (Microsoft.Data.Sqlite pools them, so this is cheap)
/// and runs on a background thread via the repositories. WAL mode lets reads and writes overlap, and a busy
/// timeout makes a second AJBrowser window wait instead of failing.
/// </para>
/// </summary>
public sealed class Database
{
    // SQLite result codes that mean "this file is not a usable database".
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    private readonly string _connectionString;

    public Database(string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        File = Path.GetFullPath(file);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = File,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 5, // seconds to wait for a lock held by another connection
            Pooling = true,
        }.ToString();
    }

    public string File { get; }

    /// <summary>
    /// Creates or upgrades the schema. If the file is corrupt (or isn't a database at all), it is moved aside
    /// as <c>ajbrowser.db.corrupt-&lt;time&gt;</c> and a fresh database is created, so the app still starts.
    /// Call once at startup, before using any repository. Synchronous: call it from a background thread or before the UI exists.
    /// </summary>
    public DatabaseOpenResult Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        try
        {
            return new DatabaseOpenResult(Migrate(), false, null);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
        {
            var backup = MoveAsideCorruptFile();
            return new DatabaseOpenResult(Migrate(), true, backup);
        }
    }

    /// <summary>Opens a connection with the per-connection settings applied. The caller disposes it.</summary>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private int Migrate()
    {
        using var connection = Open();
        using (var wal = connection.CreateCommand())
        {
            // WAL is stored in the file, so setting it once is enough; repeating it is harmless.
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            wal.ExecuteNonQuery();
        }

        return Migrations.Apply(connection);
    }

    private string MoveAsideCorruptFile()
    {
        SqliteConnection.ClearAllPools(); // release pooled handles so the file can be moved
        var backup = $"{File}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
        System.IO.File.Move(File, backup);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (System.IO.File.Exists(File + suffix))
            {
                System.IO.File.Move(File + suffix, backup + suffix);
            }
        }

        return backup;
    }
}
