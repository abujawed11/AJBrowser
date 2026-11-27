using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AJBrowser.Core.Blocking;
using AJBrowser.Core.Logging;
using AJBrowser.Core.Settings;
using AJBrowser.Core.Tests.Logging;

namespace AJBrowser.Core.Tests.Blocking;

/// <summary>A fake web server for lists: serves texts with ETags, answers 304 to a matching If-None-Match.</summary>
internal sealed class FakeListServer : HttpMessageHandler
{
    private int _version;

    public ConcurrentDictionary<string, (string Text, string ETag)> Files { get; } = new();
    public ConcurrentDictionary<string, HttpStatusCode> Failing { get; } = new();
    public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

    public void Set(string url, string text) => Files[url] = (text, $"\"v{Interlocked.Increment(ref _version)}\"");

    public int CountFor(string url) => Requests.Count(r => r.RequestUri!.AbsoluteUri == url);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        var url = request.RequestUri!.AbsoluteUri;
        if (Failing.TryGetValue(url, out var status))
        {
            return Task.FromResult(new HttpResponseMessage(status));
        }

        if (!Files.TryGetValue(url, out var file))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        if (request.Headers.IfNoneMatch.Any(t => t.Tag == file.ETag))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(file.Text, Encoding.UTF8, "text/plain") };
        response.Headers.ETag = new EntityTagHeaderValue(file.ETag);
        return Task.FromResult(response);
    }
}

public sealed class FilterListPreprocessorTests
{
    private static FilterListPreprocessor.Result Run(string text) => FilterListPreprocessor.Process(text.Replace("\r", ""));

    private static string[] Rules(string text) => Run(text).Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void Keeps_rules_for_this_browser_and_drops_the_others()
    {
        var rules = Rules("""
            a
            !#if env_chromium
            chromium-only
            !#else
            not-chromium
            !#endif
            !#if env_mobile
            mobile-only
            !#else
            desktop-only
            !#endif
            !#if !ext_ubol
            not-ubol
            !#endif
            !#if env_firefox || env_edge
            firefox-or-edge
            !#endif
            !#if env_chromium && env_mobile
            chromium-and-mobile
            !#endif
            !#if some_future_token
            unknown
            !#endif
            z
            """);
        Assert.Equal(["a", "chromium-only", "desktop-only", "not-ubol", "firefox-or-edge", "z"], rules);
    }

    [Fact]
    public void Nested_blocks_follow_their_parents()
    {
        var rules = Rules("""
            !#if env_mobile
            !#if env_chromium
            mobile-chromium
            !#else
            mobile-other
            !#endif
            !#else
            !#if env_chromium
            desktop-chromium
            !#endif
            !#endif
            """);
        Assert.Equal(["desktop-chromium"], rules);
    }

    [Fact]
    public void Stray_and_unclosed_directives_dont_break_parsing()
    {
        Assert.Equal(["a", "b"], Rules("a\n!#endif\n!#else\nb\n!#if env_mobile\nc\n"));
        // A second !#else in one block is ignored: the else branch simply continues.
        Assert.Equal(["x", "z"], Rules("!#if env_mobile\ny\n!#else\nx\n!#else\nz\n!#endif"));
        // An empty include is skipped, never fetched.
        Assert.Empty(Run("!#include \n||a.test^").Includes);
    }

    [Fact]
    public void Includes_come_only_from_active_parts_and_directives_are_removed()
    {
        var result = Run("""
            ! Title: Test
            !#include filters-2025.txt
            !#if env_mobile
            !#include filters-mobile.txt
            !#endif
            ||ads.example^
            """);
        Assert.Equal(["filters-2025.txt"], result.Includes);
        Assert.DoesNotContain("!#", result.Text);
        Assert.Contains("||ads.example^", result.Text);
        Assert.Equal("Test", result.Title);
    }

    [Theory]
    [InlineData("! Expires: 4 days", 96)]
    [InlineData("! Expires: 1 day (update frequency)", 24)]
    [InlineData("! Expires: 8 hours", 8)]
    [InlineData("!Expires:5", 120)]
    [InlineData("! Expires: 10 minutes", null)]
    [InlineData("! Expires: soon", null)]
    [InlineData("! Expires: 0 days", null)]
    public void Reads_the_expiry_from_the_header(string line, int? hours)
    {
        var expires = Run("[Adblock Plus 2.0]\n" + line + "\n||a.test^").Expires;
        Assert.Equal(hours is null ? null : TimeSpan.FromHours(hours.Value), expires);
    }

    [Fact]
    public void Header_ends_at_the_first_rule()
    {
        Assert.Null(Run("||a.test^\n! Expires: 1 day").Expires);
    }
}

public sealed class FilterListCatalogTests
{
    [Fact]
    public void Every_address_is_https_and_only_ubo_lists_are_trusted()
    {
        foreach (var list in FilterListCatalog.All)
        {
            Assert.NotEmpty(list.Urls);
            Assert.All(list.Urls, u => Assert.StartsWith("https://", u));
            Assert.Equal(list.Id.StartsWith("ubo-", StringComparison.Ordinal), list.Trusted);
        }

        Assert.Equal(FilterListCatalog.All.Count, FilterListCatalog.All.Select(l => l.Id).Distinct().Count());
    }

    [Fact]
    public void Normalize_keeps_known_ids_in_catalogue_order()
    {
        Assert.Equal(FilterListCatalog.DefaultIds, FilterListCatalog.Normalize(null));
        Assert.Empty(FilterListCatalog.Normalize([]));
        Assert.Equal(["ubo-filters", "easylist"], FilterListCatalog.Normalize(["easylist", "nope", "UBO-FILTERS", "easylist"]));
    }

    [Fact]
    public void Settings_drop_unknown_list_ids_but_keep_an_empty_choice()
    {
        var settings = new AppSettings { EnabledFilterLists = ["easylist", "gone-list"] }.Normalize();
        Assert.Equal(["easylist"], settings.EnabledFilterLists);
        Assert.Empty(new AppSettings { EnabledFilterLists = [] }.Normalize().EnabledFilterLists!);
        Assert.Null(new AppSettings().Normalize().EnabledFilterLists);
    }
}

public sealed class FilterListDownloaderTests : IDisposable
{
    private const string Main = "https://lists.test/filters/main.txt";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AJBrowserTests", Guid.NewGuid().ToString("N"));
    private readonly FakeListServer _server = new();
    private readonly FilterListDownloader _downloader;

    public FilterListDownloaderTests() =>
        _downloader = new FilterListDownloader(new HttpClient(_server), Path.Combine(_root, "raw"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static FilterListInfo List(params string[] urls) => new("test", "Test list", "", urls, false, true, "");

    [Fact]
    public async Task Inlines_includes_including_nested_ones()
    {
        _server.Set(Main, "! Title: Main\n! Expires: 2 days\n!#include part-a.txt\n||main.test^");
        _server.Set("https://lists.test/filters/part-a.txt", "||a.test^\n!#include sub/part-b.txt");
        _server.Set("https://lists.test/filters/sub/part-b.txt", "||b.test^");

        var list = await _downloader.DownloadAsync(List(Main));

        Assert.Contains("||main.test^", list.Text);
        Assert.Contains("||a.test^", list.Text);
        Assert.Contains("||b.test^", list.Text);
        Assert.Equal(3, list.Parts);
        Assert.Equal("Main", list.Title);
        Assert.Equal(TimeSpan.FromDays(2), list.Expires);
    }

    [Fact]
    public async Task Includes_in_inactive_blocks_are_not_fetched()
    {
        _server.Set(Main, "!#if env_mobile\n!#include mobile.txt\n!#endif\n||main.test^");
        await _downloader.DownloadAsync(List(Main));
        Assert.Equal(0, _server.CountFor("https://lists.test/filters/mobile.txt"));
    }

    [Fact]
    public async Task Unchanged_files_are_answered_from_the_cache_with_304()
    {
        _server.Set(Main, "!#include part.txt\n||main.test^");
        _server.Set("https://lists.test/filters/part.txt", "||part.test^");
        var first = await _downloader.DownloadAsync(List(Main));

        var second = await _downloader.DownloadAsync(List(Main));

        Assert.Equal(first.Text, second.Text);
        var conditional = _server.Requests.Skip(2).ToArray();
        Assert.Equal(2, conditional.Length);
        Assert.All(conditional, r => Assert.NotEmpty(r.Headers.IfNoneMatch));
    }

    [Fact]
    public async Task A_changed_include_is_picked_up_even_when_the_main_file_is_unchanged()
    {
        _server.Set(Main, "!#include part.txt");
        _server.Set("https://lists.test/filters/part.txt", "||old.test^");
        await _downloader.DownloadAsync(List(Main));
        _server.Set("https://lists.test/filters/part.txt", "||new.test^");

        var list = await _downloader.DownloadAsync(List(Main));

        Assert.Contains("||new.test^", list.Text);
        Assert.DoesNotContain("||old.test^", list.Text);
    }

    [Fact]
    public async Task Falls_back_to_a_mirror()
    {
        const string mirror = "https://mirror.test/main.txt";
        _server.Failing[Main] = HttpStatusCode.ServiceUnavailable;
        _server.Set(mirror, "||mirror.test^");

        var list = await _downloader.DownloadAsync(List(Main, mirror));

        Assert.Equal(mirror, list.SourceUrl);
        Assert.Contains("||mirror.test^", list.Text);
    }

    [Fact]
    public async Task Html_answers_are_refused_with_a_sentence()
    {
        _server.Set(Main, "<!DOCTYPE html><html><body>Sign in to Wi-Fi</body></html>");
        var ex = await Assert.ThrowsAsync<FilterListDownloadException>(() => _downloader.DownloadAsync(List(Main)));
        Assert.StartsWith("Test list couldn't be downloaded:", ex.Message);
        Assert.Contains("web page", ex.Message);
    }

    [Fact]
    public async Task Failure_on_every_address_says_why()
    {
        _server.Failing[Main] = HttpStatusCode.InternalServerError;
        var ex = await Assert.ThrowsAsync<FilterListDownloadException>(() => _downloader.DownloadAsync(List(Main)));
        Assert.Contains("500", ex.Message);
    }

    [Fact]
    public async Task Plain_http_is_refused()
    {
        await Assert.ThrowsAsync<FilterListDownloadException>(() => _downloader.DownloadAsync(List("http://lists.test/main.txt")));
        Assert.Empty(_server.Requests);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("https://evil.test/list.txt")]
    [InlineData("//evil.test/list.txt")]
    [InlineData("sub\\x.txt")]
    [InlineData("x.txt?y=1")]
    public async Task Unsafe_include_names_fail_the_list(string include)
    {
        _server.Set(Main, $"!#include {include}\n||main.test^");
        await Assert.ThrowsAsync<FilterListDownloadException>(() => _downloader.DownloadAsync(List(Main)));
        Assert.Equal(1, _server.Requests.Count);
    }

    [Fact]
    public async Task A_missing_include_fails_the_list_instead_of_returning_part_of_it()
    {
        _server.Set(Main, "!#include gone.txt\n||main.test^");
        await Assert.ThrowsAsync<FilterListDownloadException>(() => _downloader.DownloadAsync(List(Main)));
    }

    [Fact]
    public async Task Include_loops_are_fetched_once()
    {
        _server.Set(Main, "!#include a.txt");
        _server.Set("https://lists.test/filters/a.txt", "!#include main.txt\n||a.test^");
        var list = await _downloader.DownloadAsync(List(Main));
        Assert.Equal(2, list.Parts);
        Assert.Equal(1, _server.CountFor(Main));
    }
}

public sealed class FilterEngineServiceTests : IDisposable
{
    private static readonly FilterListInfo UboFilters = FilterListCatalog.Find("ubo-filters")!;
    private static readonly FilterListInfo EasyList = FilterListCatalog.Find("easylist")!;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "AJBrowserTests", Guid.NewGuid().ToString("N"));
    private readonly FakeListServer _server = new();
    private readonly ListSink _logs = new();
    private DateTimeOffset _now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
    private readonly List<FilterEngineService> _services = [];

    public FilterEngineServiceTests()
    {
        _server.Set(UboFilters.Urls[0], "! Expires: 1 day\n||ubo-blocked.test^");
        _server.Set(EasyList.Urls[0], "! Expires: 4 days\n||easylist-blocked.test^");
    }

    public void Dispose()
    {
        foreach (var service in _services)
        {
            service.Dispose();
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Filters => Path.Combine(_root, "Filters");

    private FilterEngineService Create(string? resources = null)
    {
        var downloader = new FilterListDownloader(new HttpClient(_server), Path.Combine(Filters, "raw"));
        var service = new FilterEngineService(downloader, Filters, resources, new AppLogger(_logs, "Filters"), () => _now);
        _services.Add(service);
        return service;
    }

    private static bool Blocks(FilterEngineService service, string host) =>
        service.Engine?.Check($"https://{host}/x.js", "https://page.test/", RequestKind.Script).ShouldBlock ?? false;

    private static readonly string[] Two = ["ubo-filters", "easylist"];

    [Fact]
    public async Task First_run_has_no_engine_until_lists_are_downloaded()
    {
        var service = Create();
        await service.InitializeAsync(Two);
        Assert.Null(service.Engine);
        Assert.Empty(_server.Requests);

        var summary = await service.UpdateAsync(force: false);

        Assert.Equal(new FilterUpdateSummary(2, 2, 0, true), summary);
        Assert.True(Blocks(service, "ubo-blocked.test"));
        Assert.True(Blocks(service, "easylist-blocked.test"));
        Assert.False(Blocks(service, "fine.test"));
        var status = service.GetStatus().Single(s => s.Info.Id == "easylist");
        Assert.True(status.Enabled);
        Assert.Equal(1, status.Rules);
        Assert.Equal(_now, status.UpdatedAt);
        Assert.Null(status.Error);
    }

    [Fact]
    public async Task Lists_are_only_checked_again_after_they_expire()
    {
        var service = Create();
        await service.InitializeAsync(Two);
        await service.UpdateAsync(force: false);
        var requests = _server.Requests.Count;

        _now += TimeSpan.FromHours(23);
        Assert.Equal(0, (await service.UpdateAsync(force: false)).Checked);
        Assert.Equal(requests, _server.Requests.Count);

        _now += TimeSpan.FromHours(2); // uBO's list (1 day) is due, EasyList (4 days) isn't
        var summary = await service.UpdateAsync(force: false);
        Assert.Equal(new FilterUpdateSummary(1, 0, 0, false), summary);
    }

    [Fact]
    public async Task Changed_content_rebuilds_and_swaps_the_engine()
    {
        var service = Create();
        await service.InitializeAsync(Two);
        await service.UpdateAsync(force: false);
        var old = service.Engine!;
        var changes = 0;
        service.EngineChanged += (_, _) => changes++;

        _server.Set(EasyList.Urls[0], "||new-ad.test^");
        var summary = await service.UpdateAsync(force: true);

        Assert.True(summary.Rebuilt);
        Assert.Equal(1, summary.Changed);
        Assert.Equal(1, changes);
        Assert.NotSame(old, service.Engine);
        Assert.True(Blocks(service, "new-ad.test"));
        Assert.False(Blocks(service, "easylist-blocked.test"));
        // The replaced engine was disposed: answering "allow" instead of crashing.
        Assert.False(old.Check("https://new-ad.test/x.js", "https://page.test/", RequestKind.Script).ShouldBlock);
    }

    [Fact]
    public async Task Next_start_loads_the_saved_engine_without_any_download()
    {
        var first = Create();
        await first.InitializeAsync(Two);
        await first.UpdateAsync(force: false);
        Assert.True(File.Exists(Path.Combine(Filters, "engine.dat")));
        var requests = _server.Requests.Count;

        var second = Create();
        await second.InitializeAsync(Two);

        Assert.True(Blocks(second, "ubo-blocked.test"));
        Assert.Equal(requests, _server.Requests.Count);
        Assert.Single(_logs.Entries, e => e.Message.Contains("built from", StringComparison.Ordinal)); // only the first run compiled
    }

    [Fact]
    public async Task A_damaged_saved_engine_is_rebuilt_from_the_saved_lists()
    {
        var first = Create();
        await first.InitializeAsync(Two);
        await first.UpdateAsync(force: false);
        await File.WriteAllTextAsync(Path.Combine(Filters, "engine.dat"), "garbage");
        var requests = _server.Requests.Count;

        var second = Create();
        await second.InitializeAsync(Two);

        Assert.True(Blocks(second, "easylist-blocked.test"));
        Assert.Equal(requests, _server.Requests.Count);
        Assert.Contains(_logs.Entries, e => e.Message.Contains("couldn't be read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_update_keeps_the_last_good_copy_and_retries_after_an_hour()
    {
        var service = Create();
        await service.InitializeAsync(Two);
        await service.UpdateAsync(force: false);
        foreach (var url in EasyList.Urls)
        {
            _server.Failing[url] = HttpStatusCode.InternalServerError;
        }

        var summary = await service.UpdateAsync(force: true);

        Assert.Equal(1, summary.Failed);
        Assert.True(Blocks(service, "easylist-blocked.test"));
        var status = service.GetStatus().Single(s => s.Info.Id == "easylist");
        Assert.StartsWith("EasyList couldn't be downloaded", status.Error);
        Assert.Equal(1, status.Rules);

        _now += TimeSpan.FromMinutes(59);
        Assert.Equal(0, (await service.UpdateAsync(force: false)).Checked);
        _now += TimeSpan.FromMinutes(2);
        Assert.Equal(1, (await service.UpdateAsync(force: false)).Checked);
    }

    [Fact]
    public async Task Changing_the_enabled_lists_downloads_new_ones_and_rebuilds()
    {
        var service = Create();
        await service.InitializeAsync(["easylist"]);
        await service.UpdateAsync(force: false);
        Assert.False(Blocks(service, "ubo-blocked.test"));

        await service.SetEnabledListsAsync(Two);
        Assert.True(Blocks(service, "ubo-blocked.test"));

        await service.SetEnabledListsAsync(["ubo-filters"]);
        Assert.False(Blocks(service, "easylist-blocked.test"));
        Assert.True(Blocks(service, "ubo-blocked.test"));

        await service.SetEnabledListsAsync([]);
        Assert.Null(service.Engine);
    }

    [Fact]
    public async Task Only_ubo_lists_may_use_trusted_scriptlets()
    {
        var resources = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "TestData", "ubo-resources.json"));
        _server.Set(UboFilters.Urls[0], "page.test##+js(trusted-set-constant, fromUbo, 1)");
        _server.Set(EasyList.Urls[0], "page.test##+js(trusted-set-constant, fromEasyList, 1)");
        var service = Create(resources);
        await service.InitializeAsync(Two);
        await service.UpdateAsync(force: false);

        var script = service.Engine!.GetCosmeticResources("https://page.test/").InjectedScript;

        Assert.Contains("fromUbo", script);
        Assert.DoesNotContain("fromEasyList", script);
    }

    [Fact]
    public async Task Unreadable_state_file_means_lists_are_checked_again()
    {
        var first = Create();
        await first.InitializeAsync(Two);
        await first.UpdateAsync(force: false);
        await File.WriteAllTextAsync(Path.Combine(Filters, "lists", "state.json"), "{ broken");

        var second = Create();
        await second.InitializeAsync(Two);
        var summary = await second.UpdateAsync(force: false);

        Assert.Equal(2, summary.Checked);
        Assert.True(Blocks(second, "ubo-blocked.test"));
    }

    [Theory]
    [InlineData("||a.test^\n##.ad\n#@#.ok\n! comment\n[Adblock Plus 2.0]\n\n  \nexample.com##.x", 4)]
    [InlineData("", 0)]
    public void Counts_rules_not_comments(string text, int expected) =>
        Assert.Equal(expected, FilterEngineService.CountRules(text));
}
