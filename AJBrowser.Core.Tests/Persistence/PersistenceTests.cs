using AJBrowser.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace AJBrowser.Core.Tests.Persistence;

/// <summary>Each test gets its own database file in a temp folder.</summary>
public abstract class DatabaseTestBase : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AJBrowserTests", Guid.NewGuid().ToString("N"));

    protected DatabaseTestBase()
    {
        Directory.CreateDirectory(_root);
        DbFile = Path.Combine(_root, "test.db");
    }

    protected string DbFile { get; }

    /// <summary>The per-test temp folder (deleted afterwards), for other files a test needs.</summary>
    protected string Root => _root;

    protected Database CreateInitialized()
    {
        var db = new Database(DbFile);
        db.Initialize();
        return db;
    }

    public void Dispose()
    {
        // Pooled connections keep the file open, and a download manager's last queued write may still be finishing on a
        // background thread: retry briefly instead of failing the test over its own cleanup.
        for (var attempt = 0; ; attempt++)
        {
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(50);
            }
        }
    }
}

public sealed class MigrationTests : DatabaseTestBase
{
    [Fact]
    public void Empty_database_is_migrated_to_the_latest_version()
    {
        var result = new Database(DbFile).Initialize();

        Assert.Equal(Migrations.LatestVersion, result.SchemaVersion);
        Assert.False(result.RecreatedAfterCorruption);
    }

    [Fact]
    public void Initialize_is_idempotent()
    {
        var db = new Database(DbFile);
        db.Initialize();
        var second = db.Initialize();

        Assert.Equal(Migrations.LatestVersion, second.SchemaVersion);
    }

    [Fact]
    public void Only_missing_migrations_run_on_an_older_database()
    {
        var db = new Database(DbFile);
        using var connection = db.Open();
        string[] scripts = ["CREATE TABLE a (x INTEGER);", "CREATE TABLE b (y INTEGER);"];

        Assert.Equal(1, Migrations.Apply(connection, scripts[..1]));  // an "old app" knew only migration 1
        Assert.Equal(2, Migrations.Apply(connection, scripts));       // the new app adds migration 2, without re-running 1

        Assert.Equal(2, Migrations.GetVersion(connection));
    }

    [Fact]
    public void A_v1_database_upgrades_and_keeps_its_data()
    {
        var db = new Database(DbFile);
        using (var connection = db.Open())
        {
            Migrations.Apply(connection, Migrations.Scripts[..1]); // what the first Phase 3 build created
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO history (url, title, visited_at) VALUES ('https://a.example/', 'A', 1);";
            insert.ExecuteNonQuery();
        }

        var result = db.Initialize();

        Assert.Equal(Migrations.LatestVersion, result.SchemaVersion);
        using var check = db.Open();
        using var count = check.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM history; SELECT COUNT(*) FROM favicons;";
        Assert.Equal(1L, count.ExecuteScalar());
    }

    [Fact]
    public void A_failing_migration_leaves_the_previous_version()
    {
        var db = new Database(DbFile);
        using var connection = db.Open();
        string[] scripts = ["CREATE TABLE a (x INTEGER);", "CREATE TABLE b (y INTEGER); THIS IS NOT SQL;"];

        Assert.ThrowsAny<SqliteException>(() => Migrations.Apply(connection, scripts));

        Assert.Equal(1, Migrations.GetVersion(connection));
    }

    [Fact]
    public void A_database_from_a_newer_app_is_left_alone()
    {
        var db = new Database(DbFile);
        using var connection = db.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version = 99;";
            command.ExecuteNonQuery();
        }

        Assert.Equal(99, Migrations.Apply(connection));
    }

    [Fact]
    public void A_corrupt_file_is_moved_aside_and_recreated()
    {
        File.WriteAllText(DbFile, "this is not a database, just text that is long enough to have a header....................");

        var result = new Database(DbFile).Initialize();

        Assert.True(result.RecreatedAfterCorruption);
        Assert.NotNull(result.CorruptBackupPath);
        Assert.True(File.Exists(result.CorruptBackupPath));
        Assert.Equal(Migrations.LatestVersion, result.SchemaVersion);
    }
}

public sealed class HistoryRepositoryTests : DatabaseTestBase
{
    private static readonly DateTime T0 = new(2026, 11, 26, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Records_and_lists_newest_first()
    {
        var history = new HistoryRepository(CreateInitialized());
        await history.RecordVisitAsync("https://a.example/", "A", T0);
        await history.RecordVisitAsync("https://b.example/", "B", T0.AddMinutes(1));

        var all = await history.SearchAsync(null, 10);

        Assert.Equal(["https://b.example/", "https://a.example/"], all.Select(e => e.Url));
        Assert.Equal(T0.AddMinutes(1), all[0].VisitedAtUtc);
    }

    [Fact]
    public async Task Revisits_within_the_merge_window_do_not_count()
    {
        var history = new HistoryRepository(CreateInitialized());
        await history.RecordVisitAsync("https://a.example/", "A", T0);
        await history.RecordVisitAsync("https://a.example/", "", T0.AddSeconds(5));   // reload: merged, title kept
        await history.RecordVisitAsync("https://a.example/", "A2", T0.AddMinutes(5)); // real revisit

        var entry = Assert.Single(await history.SearchAsync(null, 10));
        Assert.Equal(2, entry.VisitCount);
        Assert.Equal("A2", entry.Title);
        Assert.Equal(T0.AddMinutes(5), entry.VisitedAtUtc);
    }

    [Fact]
    public async Task Search_matches_url_or_title_and_escapes_wildcards()
    {
        var history = new HistoryRepository(CreateInitialized());
        await history.RecordVisitAsync("https://news.example/", "Daily News", T0);
        await history.RecordVisitAsync("https://shop.example/100%_off", "Sale", T0);
        await history.RecordVisitAsync("https://other.example/", "Other", T0);

        Assert.Single(await history.SearchAsync("daily", 10));
        Assert.Single(await history.SearchAsync("NEWS.example", 10));
        Assert.Single(await history.SearchAsync("100%_", 10));   // % and _ are literal, not wildcards
        Assert.Empty(await history.SearchAsync("%off", 10));     // as a wildcard this would match "100%_off"
    }

    [Fact]
    public async Task Paging_with_offset()
    {
        var history = new HistoryRepository(CreateInitialized());
        for (var i = 0; i < 5; i++)
        {
            await history.RecordVisitAsync($"https://site{i}.example/", $"S{i}", T0.AddMinutes(i));
        }

        var page2 = await history.SearchAsync(null, 2, offset: 2);

        Assert.Equal(["https://site2.example/", "https://site1.example/"], page2.Select(e => e.Url));
    }

    [Fact]
    public async Task Title_update_and_delete()
    {
        var history = new HistoryRepository(CreateInitialized());
        await history.RecordVisitAsync("https://a.example/", "", T0);
        await history.UpdateTitleAsync("https://a.example/", "Late title");

        var entry = Assert.Single(await history.SearchAsync(null, 10));
        Assert.Equal("Late title", entry.Title);

        await history.DeleteAsync(entry.Id);
        Assert.Equal(0, await history.CountAsync());
    }

    [Fact]
    public async Task Delete_range_removes_only_that_range()
    {
        var history = new HistoryRepository(CreateInitialized());
        await history.RecordVisitAsync("https://old.example/", "Old", T0.AddDays(-10));
        await history.RecordVisitAsync("https://new.example/", "New", T0);

        var deleted = await history.DeleteRangeAsync(T0.AddHours(-1), T0.AddHours(1));

        Assert.Equal(1, deleted);
        Assert.Equal("https://old.example/", Assert.Single(await history.SearchAsync(null, 10)).Url);
    }

    [Fact]
    public async Task Very_long_urls_are_not_recorded_and_titles_are_cut()
    {
        var history = new HistoryRepository(CreateInitialized());
        await history.RecordVisitAsync("https://a.example/" + new string('x', HistoryRepository.MaxUrlLength), "T", T0);
        await history.RecordVisitAsync("https://b.example/", new string('t', 5000), T0);

        var entry = Assert.Single(await history.SearchAsync(null, 10));
        Assert.Equal(HistoryRepository.MaxTitleLength, entry.Title.Length);
    }

    [Fact]
    public async Task Suggestion_candidates_are_ordered_by_visit_count()
    {
        var history = new HistoryRepository(CreateInitialized());
        await history.RecordVisitAsync("https://rare.example/", "Rare", T0);
        for (var i = 0; i < 3; i++)
        {
            await history.RecordVisitAsync("https://often.example/", "Often", T0.AddHours(i));
        }

        var candidates = await history.FindSuggestionCandidatesAsync("example", 10);

        Assert.Equal("https://often.example/", candidates[0].Url);
    }
}

public sealed class BookmarkRepositoryTests : DatabaseTestBase
{
    private static readonly DateTime T0 = new(2026, 11, 26, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Add_assigns_increasing_positions_per_folder()
    {
        var bookmarks = new BookmarkRepository(CreateInitialized());
        var a = await bookmarks.AddAsync(null, "A", "https://a.example/", T0);
        var folder = await bookmarks.AddAsync(null, "Folder", null, T0);
        var inside = await bookmarks.AddAsync(folder.Id, "In", "https://in.example/", T0);

        Assert.Equal(0, a.Position);
        Assert.Equal(1, folder.Position);
        Assert.True(folder.IsFolder);
        Assert.Equal(0, inside.Position);
        Assert.Equal(3, (await bookmarks.GetAllAsync()).Count);
    }

    [Fact]
    public async Task Update_renames_and_moves_to_the_end_of_another_folder()
    {
        var bookmarks = new BookmarkRepository(CreateInitialized());
        var folder = await bookmarks.AddAsync(null, "Folder", null, T0);
        await bookmarks.AddAsync(folder.Id, "Existing", "https://x.example/", T0);
        var a = await bookmarks.AddAsync(null, "A", "https://a.example/", T0);

        var moved = await bookmarks.UpdateAsync(a, "Renamed", "https://a2.example/", folder.Id);

        Assert.Equal(folder.Id, moved.ParentId);
        Assert.Equal(1, moved.Position);
        var stored = (await bookmarks.GetAllAsync()).Single(b => b.Id == a.Id);
        Assert.Equal(("Renamed", "https://a2.example/"), (stored.Title, stored.Url));
    }

    [Fact]
    public async Task Deleting_a_folder_deletes_its_contents()
    {
        var bookmarks = new BookmarkRepository(CreateInitialized());
        var folder = await bookmarks.AddAsync(null, "Folder", null, T0);
        var sub = await bookmarks.AddAsync(folder.Id, "Sub", null, T0);
        await bookmarks.AddAsync(sub.Id, "Deep", "https://deep.example/", T0);
        await bookmarks.AddAsync(null, "Keep", "https://keep.example/", T0);

        await bookmarks.DeleteAsync(folder.Id);

        Assert.Equal("Keep", Assert.Single(await bookmarks.GetAllAsync()).Title);
    }
}

public sealed class FaviconRepositoryTests : DatabaseTestBase
{
    private static readonly DateTime T0 = new(2026, 11, 26, 10, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("https://WWW.Google.com/search?q=x", "www.google.com")]
    [InlineData("http://example.com:8080/a", "example.com")]
    [InlineData("about:blank", null)]
    [InlineData("ajbrowser://history", null)]
    [InlineData("file:///C:/a.html", null)]
    [InlineData(null, null)]
    public void Key_is_the_lower_case_host_of_web_urls(string? url, string? expected) =>
        Assert.Equal(expected, FaviconRepository.KeyFor(url));

    [Fact]
    public async Task Save_inserts_then_replaces()
    {
        var icons = new FaviconRepository(CreateInitialized());
        await icons.SaveAsync("example.com", [1, 2, 3], T0);
        await icons.SaveAsync("example.com", [4, 5], T0.AddMinutes(1));
        await icons.SaveAsync("other.example", [9], T0);

        var all = await icons.GetAllAsync();

        Assert.Equal(2, all.Count);
        Assert.Equal(new byte[] { 4, 5 }, all["example.com"]);
    }

    [Fact]
    public async Task Empty_and_oversized_icons_are_ignored()
    {
        var icons = new FaviconRepository(CreateInitialized());
        await icons.SaveAsync("a.example", [], T0);
        await icons.SaveAsync("b.example", new byte[FaviconRepository.MaxIconBytes + 1], T0);

        Assert.Empty(await icons.GetAllAsync());
    }
}

public sealed class SessionRepositoryTests : DatabaseTestBase
{
    [Fact]
    public async Task Save_replaces_the_previous_session()
    {
        var session = new SessionRepository(CreateInitialized());
        await session.SaveAsync([new("https://a.example/", "A", false), new("https://b.example/", "B", true)]);
        await session.SaveAsync([new("https://c.example/", "C", true)]);

        var loaded = await session.LoadAsync();

        Assert.Equal([new SessionTab("https://c.example/", "C", true)], loaded);
    }

    [Fact]
    public async Task Order_and_active_flag_round_trip()
    {
        var session = new SessionRepository(CreateInitialized());
        SessionTab[] tabs = [new("https://a.example/", "A", false), new("ajbrowser://history", "History", true), new("https://c.example/", "", false)];
        session.Save(tabs);

        Assert.Equal(tabs, await session.LoadAsync());
    }
}
