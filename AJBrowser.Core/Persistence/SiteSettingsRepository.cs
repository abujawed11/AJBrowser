namespace AJBrowser.Core.Persistence;

/// <summary>Per-site settings (schema v7). For now: the sites where ad blocking is turned off.</summary>
public sealed class SiteSettingsRepository
{
    private readonly Database _database;

    public SiteSettingsRepository(Database database) => _database = database;

    public Task<IReadOnlyList<string>> GetAdBlockDisabledSitesAsync() => Task.Run<IReadOnlyList<string>>(() =>
    {
        using var connection = _database.Open();
        using var command = Sql.Command(connection, "SELECT site FROM site_settings WHERE adblock_disabled = 1 ORDER BY site;");
        var result = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    });

    /// <summary>Turns ad blocking off (<paramref name="disabled"/> = true) or back on for one site key.</summary>
    public Task SetAdBlockDisabledAsync(string site, bool disabled, DateTime nowUtc) => Task.Run(() =>
    {
        using var connection = _database.Open();
        using var command = disabled
            ? Sql.Command(connection,
                """
                INSERT INTO site_settings (site, adblock_disabled, updated_at) VALUES ($site, 1, $now)
                ON CONFLICT (site) DO UPDATE SET adblock_disabled = 1, updated_at = excluded.updated_at;
                """,
                ("$site", site), ("$now", Sql.ToUnixMs(nowUtc)))
            // Only setting today, so turning blocking back on removes the row.
            : Sql.Command(connection, "DELETE FROM site_settings WHERE site = $site;", ("$site", site));
        command.ExecuteNonQuery();
    });
}
