using System.Net;
using AJBrowser.Core.Downloads;
using AJBrowser.Core.Persistence;
using AJBrowser.Core.Tests.Persistence;

namespace AJBrowser.Core.Tests.Downloads;

public sealed class FileNamerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AJBrowserTests", Guid.NewGuid().ToString("N"));

    public FileNamerTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("a<b>c:d\"e|f?g*h.txt", "a_b_c_d_e_f_g_h.txt")]
    [InlineData("../../evil.exe", "evil.exe")]
    [InlineData(@"C:\Windows\x.dll", "x.dll")]
    [InlineData("name...   ", "name")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("com1.tar.gz", "_com1.tar.gz")]
    [InlineData("", "download")]
    [InlineData("...", "download")]
    [InlineData(null, "download")]
    [InlineData("tab\there", "tab_here")]
    public void Sanitizes(string? input, string expected) => Assert.Equal(expected, FileNamer.Sanitize(input));

    [Fact]
    public void Long_names_are_cut_keeping_the_extension()
    {
        var result = FileNamer.Sanitize(new string('a', 400) + ".mp4");

        Assert.Equal(FileNamer.MaxNameLength, result.Length);
        Assert.EndsWith(".mp4", result);
    }

    [Theory]
    [InlineData("https://example.com/files/movie%20night.mp4", null, null, "movie night.mp4")]
    [InlineData("https://example.com/get?id=1", "attachment; filename=\"report.pdf\"", null, "report.pdf")]
    [InlineData("https://example.com/get", "attachment; filename*=UTF-8''r%C3%A9sum%C3%A9.pdf", null, "résumé.pdf")]
    [InlineData("https://example.com/video", null, "video/mp4; codecs=avc1", "video.mp4")]
    [InlineData("https://example.com/", null, null, "download")]
    [InlineData("https://example.com/x", "attachment; filename=\"..\\..\\evil.bat\"", null, "evil.bat")]
    public void Suggests_names(string url, string? disposition, string? contentType, string expected) =>
        Assert.Equal(expected, FileNamer.Suggest(new Uri(url), disposition, contentType));

    [Fact]
    public void Unique_path_counts_up_and_respects_partial_files()
    {
        File.WriteAllText(Path.Combine(_root, "a.mp4"), "x");
        File.WriteAllText(Path.Combine(_root, "a (1).mp4.ajpart"), "x");

        var path = FileNamer.UniquePath(_root, "a.mp4", File.Exists);

        Assert.Equal(Path.Combine(_root, "a (2).mp4"), path);
    }

    [Fact]
    public void Reservations_keep_concurrent_downloads_apart_until_released()
    {
        var namer = new FileNamer();

        var first = namer.ReservePath(_root, "same.zip");
        var second = namer.ReservePath(_root, "same.zip");
        namer.Release(first);
        var third = namer.ReservePath(_root, "same.zip");

        Assert.Equal(Path.Combine(_root, "same.zip"), first);
        Assert.Equal(Path.Combine(_root, "same (1).zip"), second);
        Assert.Equal(first, third);
    }
}

public sealed class DownloadStateMachineTests
{
    [Theory]
    [InlineData(DownloadState.Queued, DownloadState.Downloading)]
    [InlineData(DownloadState.Downloading, DownloadState.Paused)]
    [InlineData(DownloadState.Downloading, DownloadState.Completed)]
    [InlineData(DownloadState.Paused, DownloadState.Downloading)]
    [InlineData(DownloadState.Failed, DownloadState.Queued)]
    [InlineData(DownloadState.Interrupted, DownloadState.Queued)]
    [InlineData(DownloadState.Interrupted, DownloadState.Downloading)]
    [InlineData(DownloadState.Downloading, DownloadState.Downloading)]
    public void Allows(DownloadState from, DownloadState to) => Assert.True(DownloadStateMachine.CanMove(from, to));

    [Theory]
    [InlineData(DownloadState.Completed, DownloadState.Downloading)]
    [InlineData(DownloadState.Completed, DownloadState.Queued)]
    [InlineData(DownloadState.Cancelled, DownloadState.Downloading)]
    [InlineData(DownloadState.Queued, DownloadState.Completed)]
    [InlineData(DownloadState.Failed, DownloadState.Completed)]
    public void Refuses(DownloadState from, DownloadState to) => Assert.False(DownloadStateMachine.CanMove(from, to));

    [Fact]
    public void Item_flags_follow_the_state()
    {
        var item = new DownloadItem { SourceUrl = "https://x.example/a?token=secret", FilePath = @"C:\d\a.bin", Kind = DownloadKind.App, State = DownloadState.Downloading, TotalBytes = 200, ReceivedBytes = 50 };

        Assert.True(item.CanPause);
        Assert.True(item.CanCancel);
        Assert.False(item.CanRetry);
        Assert.Equal(0.25, item.Fraction);
        Assert.Equal("a.bin", item.FileName);
        Assert.DoesNotContain("secret", item.DisplayUrl);
        Assert.Null((item with { TotalBytes = null }).Fraction);

        // WebView2 reports CanResume = false while a download runs; Pause must still be offered (your bug report, 4b).
        var browser = item with { Kind = DownloadKind.Browser, BrowserCanResume = false };
        Assert.True(browser.CanPause);
        Assert.True((browser with { State = DownloadState.Paused }).CanResumeNow); // the user paused it: always resumable
        Assert.False((browser with { State = DownloadState.Interrupted }).CanResumeNow); // an error: needs CanResume
        Assert.True((browser with { State = DownloadState.Interrupted, BrowserCanResume = true }).CanResumeNow);
    }
}

public sealed class TransferRateEstimatorTests
{
    private static readonly DateTime T0 = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Speed_and_time_left()
    {
        var rate = new TransferRateEstimator();
        rate.Add(T0, 0);
        rate.Add(T0.AddSeconds(2), 2_000_000);

        Assert.Equal(1_000_000, rate.BytesPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(8), rate.Remaining(10_000_000));
    }

    [Fact]
    public void Unknown_until_two_samples_and_without_total()
    {
        var rate = new TransferRateEstimator();
        Assert.Null(rate.BytesPerSecond);

        rate.Add(T0, 0);
        rate.Add(T0.AddMilliseconds(100), 100);
        Assert.Null(rate.BytesPerSecond); // too short to judge

        rate.Add(T0.AddSeconds(1), 1000);
        Assert.Null(rate.Remaining(null));
    }

    [Fact]
    public void Old_samples_drop_out_of_the_window()
    {
        var rate = new TransferRateEstimator(TimeSpan.FromSeconds(5));
        rate.Add(T0, 0);
        rate.Add(T0.AddSeconds(1), 10_000_000);  // fast burst
        rate.Add(T0.AddSeconds(10), 10_100_000); // then slow
        rate.Add(T0.AddSeconds(12), 10_300_000);

        Assert.Equal(100_000, rate.BytesPerSecond!.Value, precision: 0);
    }

    [Fact]
    public void A_restart_from_zero_resets()
    {
        var rate = new TransferRateEstimator();
        rate.Add(T0, 5000);
        rate.Add(T0.AddSeconds(1), 6000);
        rate.Add(T0.AddSeconds(2), 0);

        Assert.Null(rate.BytesPerSecond);
    }
}

public sealed class HttpFileDownloaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AJBrowserTests", Guid.NewGuid().ToString("N"));
    private readonly FakeFileServer _server = new() { Content = FakeFileServer.MakeContent(300_000) };
    private readonly List<TimeSpan> _waits = [];

    public HttpFileDownloaderTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Target => Path.Combine(_root, "file.bin");

    private HttpFileDownloader Downloader(TimeSpan? stall = null) =>
        new(new HttpClient(_server), (wait, _) =>
        {
            _waits.Add(wait);
            return Task.CompletedTask;
        })
        {
            StallTimeout = stall ?? TimeSpan.FromSeconds(30),
        };

    private Task<DownloadResult> Run(HttpFileDownloader downloader, ResumeInfo? previous = null, CancellationToken token = default) =>
        downloader.DownloadAsync(new DownloadRequest(new Uri("https://files.example/file.bin"), Target), previous, null, token);

    [Fact]
    public async Task Full_download_is_renamed_from_ajpart_when_complete()
    {
        var result = await Run(Downloader());

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.Equal(_server.Content, await File.ReadAllBytesAsync(Target));
        Assert.False(File.Exists(Target + ".ajpart"));
        Assert.Equal(300_000, result.TotalBytes);
        Assert.True(result.Resume!.CanResume);
    }

    [Fact]
    public async Task Resumes_an_existing_partial_file_with_a_range_request()
    {
        await File.WriteAllBytesAsync(Target + ".ajpart", _server.Content[..100_000]);

        var result = await Run(Downloader(), new ResumeInfo(true, "\"v1\"", null));

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.Equal(_server.Content, await File.ReadAllBytesAsync(Target));
        Assert.Equal(100_000, _server.Requests[0].Headers.Range!.Ranges.First().From);
        Assert.Equal("\"v1\"", _server.Requests[0].Headers.IfRange!.EntityTag!.ToString());
    }

    [Fact]
    public async Task Starts_over_when_the_server_ignores_the_range()
    {
        _server.SupportsRanges = false;
        await File.WriteAllBytesAsync(Target + ".ajpart", new byte[100_000]); // junk that must not survive

        var result = await Run(Downloader(), new ResumeInfo(true, "\"v1\"", null));

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.Equal(_server.Content, await File.ReadAllBytesAsync(Target));
    }

    [Fact]
    public async Task Starts_over_when_the_file_changed_on_the_server()
    {
        await File.WriteAllBytesAsync(Target + ".ajpart", new byte[100_000]);
        _server.ETag = "\"v2\""; // If-Range "v1" no longer matches → full body

        var result = await Run(Downloader(), new ResumeInfo(true, "\"v1\"", null));

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.Equal(_server.Content, await File.ReadAllBytesAsync(Target));
    }

    [Fact]
    public async Task A_dropped_connection_is_retried_and_continues_where_it_stopped()
    {
        _server.DropAfterBytes = 120_000;

        var result = await Run(Downloader());

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.Equal(_server.Content, await File.ReadAllBytesAsync(Target));
        Assert.Equal(2, _server.Requests.Count);
        Assert.Equal(120_000, _server.Requests[1].Headers.Range!.Ranges.First().From);
        Assert.Equal([TimeSpan.FromSeconds(1)], _waits);
    }

    [Fact]
    public async Task Server_errors_are_retried_with_growing_waits_then_give_up()
    {
        for (var i = 0; i < 10; i++)
        {
            _server.ScriptedStatuses.Enqueue(HttpStatusCode.ServiceUnavailable);
        }

        var result = await Run(Downloader());

        Assert.Equal(DownloadOutcome.Failed, result.Outcome);
        Assert.Contains("503", result.Error);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], _waits);
        Assert.False(File.Exists(Target));
    }

    [Fact]
    public async Task Client_errors_fail_at_once_without_the_url_in_the_message()
    {
        _server.ScriptedStatuses.Enqueue(HttpStatusCode.NotFound);

        var result = await Run(Downloader());

        Assert.Equal(DownloadOutcome.Failed, result.Outcome);
        Assert.Contains("404", result.Error);
        Assert.DoesNotContain("files.example", result.Error);
        Assert.Single(_server.Requests);
    }

    [Fact]
    public async Task A_stall_counts_as_a_network_error()
    {
        _server.StallNext = true;

        var result = await Run(Downloader(stall: TimeSpan.FromMilliseconds(200)));

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.Equal(2, _server.Requests.Count);
    }

    [Fact]
    public async Task Cancelling_keeps_the_partial_file_and_DeletePartial_removes_it()
    {
        _server.StallNext = true; // keeps the download open until we cancel
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var result = await Run(Downloader(), token: cts.Token);

        Assert.Equal(DownloadOutcome.Cancelled, result.Outcome);
        Assert.True(File.Exists(Target + ".ajpart"));
        Assert.False(File.Exists(Target));

        HttpFileDownloader.DeletePartial(Target);
        Assert.False(File.Exists(Target + ".ajpart"));
    }

    [Fact]
    public async Task Never_overwrites_an_existing_target()
    {
        await File.WriteAllTextAsync(Target, "someone else's file");

        var result = await Run(Downloader());

        Assert.Equal(DownloadOutcome.Failed, result.Outcome);
        Assert.Equal("someone else's file", await File.ReadAllTextAsync(Target));
    }
}

public sealed class DownloadRepositoryTests : DatabaseTestBase
{
    private static readonly DateTime T0 = new(2026, 11, 26, 10, 0, 0, DateTimeKind.Utc);

    private static DownloadItem Item(DownloadKind kind, DownloadState state, int minutes = 0) => new()
    {
        Kind = kind,
        State = state,
        SourceUrl = "https://files.example/a.bin?sig=secret",
        PageUrl = "https://page.example/",
        FilePath = @"C:\Downloads\a.bin",
        TotalBytes = 1000,
        ReceivedBytes = 250,
        StartedAtUtc = T0.AddMinutes(minutes),
        Resume = new ResumeInfo(true, "\"e\"", "Wed, 26 Nov 2026 10:00:00 GMT"),
    };

    [Fact]
    public async Task Add_update_and_read_back()
    {
        var repo = new DownloadRepository(CreateInitialized());
        var added = await repo.AddAsync(Item(DownloadKind.App, DownloadState.Downloading));
        await repo.UpdateAsync(added with { State = DownloadState.Completed, ReceivedBytes = 1000, CompletedAtUtc = T0.AddMinutes(1), Error = null });

        var stored = Assert.Single(await repo.GetRecentAsync(10));

        Assert.Equal(added.Id, stored.Id);
        Assert.Equal(DownloadState.Completed, stored.State);
        Assert.Equal(1000, stored.ReceivedBytes);
        Assert.Equal(T0.AddMinutes(1), stored.CompletedAtUtc);
        Assert.Equal(new ResumeInfo(true, "\"e\"", "Wed, 26 Nov 2026 10:00:00 GMT"), stored.Resume);
        Assert.Equal("https://files.example/a.bin?sig=secret", stored.SourceUrl);
    }

    [Fact]
    public async Task Stale_downloads_are_marked_at_startup()
    {
        var repo = new DownloadRepository(CreateInitialized());
        await repo.AddAsync(Item(DownloadKind.Browser, DownloadState.Downloading, 1));
        await repo.AddAsync(Item(DownloadKind.App, DownloadState.Downloading, 2));
        await repo.AddAsync(Item(DownloadKind.App, DownloadState.Completed, 3));

        Assert.Equal(2, await repo.MarkStaleAsync());

        var byKind = (await repo.GetRecentAsync(10)).ToDictionary(i => (i.Kind, i.StartedAtUtc.Minute));
        Assert.Equal(DownloadState.Interrupted, byKind[(DownloadKind.Browser, 1)].State);
        Assert.NotNull(byKind[(DownloadKind.Browser, 1)].Error);
        Assert.Equal(DownloadState.Paused, byKind[(DownloadKind.App, 2)].State);
        Assert.Equal(DownloadState.Completed, byKind[(DownloadKind.App, 3)].State);
    }

    [Fact]
    public async Task Newest_first_and_clear_finished()
    {
        var repo = new DownloadRepository(CreateInitialized());
        await repo.AddAsync(Item(DownloadKind.App, DownloadState.Completed, 1));
        await repo.AddAsync(Item(DownloadKind.App, DownloadState.Paused, 2));
        await repo.AddAsync(Item(DownloadKind.Browser, DownloadState.Failed, 3));

        Assert.Equal([3, 2, 1], (await repo.GetRecentAsync(10)).Select(i => i.StartedAtUtc.Minute));
        Assert.Equal(2, await repo.DeleteFinishedAsync());
        Assert.Equal(DownloadState.Paused, Assert.Single(await repo.GetRecentAsync(10)).State);
    }
}

public sealed class DownloadManagerTests : DatabaseTestBase
{
    private readonly string _folder;
    private readonly FakeFileServer _server = new() { Content = FakeFileServer.MakeContent(200_000) };

    public DownloadManagerTests()
    {
        _folder = Path.Combine(Root, "downloads"); // inside the base class temp folder, so it is cleaned up too
        Directory.CreateDirectory(_folder);
    }

    private (DownloadManager Manager, DownloadRepository Repo, FileNamer Namer) Create()
    {
        var repo = new DownloadRepository(CreateInitialized());
        var namer = new FileNamer();
        var downloader = new HttpFileDownloader(new HttpClient(_server), (_, _) => Task.CompletedTask) { StallTimeout = TimeSpan.FromSeconds(5) };
        return (new DownloadManager(repo, downloader, namer), repo, namer);
    }

    private DownloadRequest Request(FileNamer namer, string name = "file.bin") =>
        new(new Uri($"https://files.example/{name}"), namer.ReservePath(_folder, name));

    private static async Task WaitFor(DownloadManager manager, long id, DownloadState state)
    {
        for (var i = 0; i < 200 && manager.Find(id)?.State != state; i++)
        {
            await Task.Delay(25);
        }

        Assert.Equal(state, manager.Find(id)?.State);
    }

    [Fact]
    public async Task An_app_download_completes_and_is_saved()
    {
        var (manager, repo, namer) = Create();

        var item = await manager.EnqueueAsync(Request(namer), "https://page.example/");
        await WaitFor(manager, item.Id, DownloadState.Completed);
        await manager.WhenIdleAsync();

        var stored = Assert.Single(await repo.GetRecentAsync(10));
        Assert.Equal(DownloadState.Completed, stored.State);
        Assert.Equal(200_000, stored.ReceivedBytes);
        Assert.Equal(_server.Content, await File.ReadAllBytesAsync(item.FilePath));
    }

    [Fact]
    public async Task Pause_keeps_the_partial_file_and_resume_finishes_it()
    {
        var (manager, _, namer) = Create();
        _server.StallNext = true; // first attempt hangs, so we can pause it

        var item = await manager.EnqueueAsync(Request(namer));
        await WaitFor(manager, item.Id, DownloadState.Downloading);

        // Pause only once the stalling first request really reached the server; pausing earlier (possible when the
        // machine is busy) would leave the stall armed for the resumed request and the test would time out.
        for (var i = 0; i < 200 && _server.Requests.Count == 0; i++)
        {
            await Task.Delay(25);
        }

        manager.Pause(item.Id);
        await WaitFor(manager, item.Id, DownloadState.Paused);

        manager.Resume(item.Id);
        await WaitFor(manager, item.Id, DownloadState.Completed);
        Assert.Equal(_server.Content, await File.ReadAllBytesAsync(item.FilePath));
    }

    [Fact]
    public async Task Cancel_deletes_the_partial_file()
    {
        var (manager, _, namer) = Create();
        _server.StallNext = true;

        var item = await manager.EnqueueAsync(Request(namer));
        await WaitFor(manager, item.Id, DownloadState.Downloading);
        manager.Cancel(item.Id);
        await WaitFor(manager, item.Id, DownloadState.Cancelled);
        await manager.WhenIdleAsync();

        Assert.False(File.Exists(item.FilePath + ".ajpart"));
        Assert.False(File.Exists(item.FilePath));
    }

    [Fact]
    public async Task Only_MaxConcurrent_run_at_once()
    {
        var (manager, _, namer) = Create();
        manager.MaxConcurrent = 1;
        _server.StallNext = true; // the first download hangs

        var first = await manager.EnqueueAsync(Request(namer, "one.bin"));
        var second = await manager.EnqueueAsync(Request(namer, "two.bin"));
        await WaitFor(manager, first.Id, DownloadState.Downloading);
        Assert.Equal(DownloadState.Queued, manager.Find(second.Id)!.State);

        manager.Cancel(first.Id);
        await WaitFor(manager, second.Id, DownloadState.Completed);
    }

    [Fact]
    public async Task Failed_download_can_be_retried()
    {
        var (manager, _, namer) = Create();
        _server.ScriptedStatuses.Enqueue(HttpStatusCode.Forbidden);

        var item = await manager.EnqueueAsync(Request(namer));
        await WaitFor(manager, item.Id, DownloadState.Failed);
        Assert.Contains("403", manager.Find(item.Id)!.Error);

        manager.Retry(item.Id);
        await WaitFor(manager, item.Id, DownloadState.Completed);
    }

    [Fact]
    public async Task Expired_link_says_so_and_a_fresh_address_lets_retry_finish()
    {
        var (manager, _, namer) = Create();
        _server.ScriptedStatuses.Enqueue(HttpStatusCode.Forbidden);

        var item = await manager.EnqueueAsync(Request(namer));
        await WaitFor(manager, item.Id, DownloadState.Failed);
        Assert.Contains("expired", manager.Find(item.Id)!.Error);

        var fresh = new Uri("https://files.example/file.bin?token=new");
        Assert.True(manager.ReplaceSource(item.Id, fresh, new Dictionary<string, string> { ["Referer"] = "https://site.example/" }));
        Assert.Equal(fresh.AbsoluteUri, manager.Find(item.Id)!.SourceUrl);

        manager.Retry(item.Id);
        await WaitFor(manager, item.Id, DownloadState.Completed);
        Assert.False(manager.ReplaceSource(item.Id, fresh, null)); // finished downloads keep their address
    }

    [Fact]
    public async Task Browser_downloads_are_tracked_and_late_reports_ignored()
    {
        var (manager, repo, _) = Create();
        var changes = new List<DownloadItem>();
        manager.ItemChanged += (_, item) => changes.Add(item);

        var item = await manager.AddBrowserDownloadAsync("https://files.example/b.zip", null, Path.Combine(_folder, "b.zip"), 1000);
        manager.UpdateBrowserDownload(item.Id, DownloadState.Downloading, 500, 1000, canResume: true);
        manager.UpdateBrowserDownload(item.Id, DownloadState.Completed, 1000, 1000, canResume: false);
        manager.UpdateBrowserDownload(item.Id, DownloadState.Downloading, 900, 1000, canResume: true); // stale: refused
        await manager.WhenIdleAsync();

        Assert.Equal(DownloadState.Completed, manager.Find(item.Id)!.State);
        Assert.NotNull(manager.Find(item.Id)!.CompletedAtUtc);
        Assert.Equal(DownloadState.Completed, Assert.Single(await repo.GetRecentAsync(10)).State);
        Assert.Contains(changes, c => c.State == DownloadState.Completed);
    }

    [Fact]
    public async Task Startup_marks_stale_items_and_clear_finished_removes_them()
    {
        var (manager, repo, _) = Create();
        await repo.AddAsync(new DownloadItem { Kind = DownloadKind.Browser, State = DownloadState.Downloading, SourceUrl = "https://x.example/a", FilePath = @"C:\d\a", StartedAtUtc = DateTime.UtcNow });
        await repo.AddAsync(new DownloadItem { Kind = DownloadKind.App, State = DownloadState.Downloading, SourceUrl = "https://x.example/b", FilePath = @"C:\d\b", StartedAtUtc = DateTime.UtcNow });

        await manager.InitializeAsync();

        Assert.Contains(manager.Items, i => i.Kind == DownloadKind.Browser && i.State == DownloadState.Interrupted);
        Assert.Contains(manager.Items, i => i.Kind == DownloadKind.App && i.State == DownloadState.Paused);

        Assert.Equal(1, await manager.ClearFinishedAsync());
        Assert.Equal(DownloadState.Paused, Assert.Single(manager.Items).State);
    }
}
