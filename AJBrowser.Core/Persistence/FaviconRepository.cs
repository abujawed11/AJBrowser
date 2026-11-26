namespace AJBrowser.Core.Persistence;

/// <summary>
/// Site icons (favicons), one PNG per host. The app loads them all at startup (they are small: usually 1–5 KB,
/// at most <see cref="MaxIconBytes"/>) and saves new ones as tabs receive them.
/// </summary>
public sealed class FaviconRepository
{
    /// <summary>Bigger icons are not stored (a real favicon is tiny; anything huge is a mistake or abuse).</summary>
    public const int MaxIconBytes = 64 * 1024;

    private readonly Database _database;

    public FaviconRepository(Database database) => _database = database;

    /// <summary>The key icons are stored under: the lower-case host of an http(s) URL, or null for anything else.</summary>
    public static string? KeyFor(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && uri.Host.Length > 0
            ? uri.Host.ToLowerInvariant()
            : null;

    public Task<IReadOnlyDictionary<string, byte[]>> GetAllAsync() => Task.Run<IReadOnlyDictionary<string, byte[]>>(() =>
    {
        using var connection = _database.Open();
        using var command = Sql.Command(connection, "SELECT host, png FROM favicons;");
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = (byte[])reader.GetValue(1);
        }

        return result;
    });

    /// <summary>Inserts or replaces the icon for <paramref name="host"/>. Empty or oversized icons are ignored.</summary>
    public Task SaveAsync(string host, byte[] png, DateTime updatedAtUtc) => Task.Run(() =>
    {
        if (png.Length == 0 || png.Length > MaxIconBytes)
        {
            return;
        }

        using var connection = _database.Open();
        using var command = Sql.Command(connection,
            """
            INSERT INTO favicons (host, png, updated_at) VALUES ($host, $png, $now)
            ON CONFLICT (host) DO UPDATE SET png = excluded.png, updated_at = excluded.updated_at;
            """,
            ("$host", host), ("$png", png), ("$now", Sql.ToUnixMs(updatedAtUtc)));
        command.ExecuteNonQuery();
    });
}
