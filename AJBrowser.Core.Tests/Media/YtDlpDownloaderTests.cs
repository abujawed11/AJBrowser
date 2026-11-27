using System.Diagnostics;
using AJBrowser.Core.Downloads;
using AJBrowser.Core.Media;
using AJBrowser.Core.Media.Dash;
using AJBrowser.Core.Media.Hls;
using AJBrowser.Core.Media.YtDlp;
using AJBrowser.Core.Settings;
using AJBrowser.Core.Tools;

namespace AJBrowser.Core.Tests.Media;

public sealed class YtDlpDownloaderTests : IDisposable
{
    private static readonly Uri Page = new("https://www.youtube.com/watch?v=abc123XYZ00");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AJBrowserTests", Guid.NewGuid().ToString("N"));
    private readonly List<IReadOnlyList<string>> _calls = [];
    private string? _ytdlp = @"C:\tools\yt-dlp.exe";
    private string? _ffmpeg = @"C:\tools\ffmpeg.exe";
    private Func<IReadOnlyList<string>, Action<string>?, CancellationToken, Task<ProcessResult>> _behaviour = (_, _, _) => Task.FromResult(new ProcessResult(0, [], []));
    private Func<IReadOnlyList<Uri>, Task<BrowserSession>> _session;
    private IReadOnlyList<Uri>? _sessionUrls;

    public YtDlpDownloaderTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "out"));
        _session = urls =>
        {
            _sessionUrls = urls;
            return Task.FromResult(new BrowserSession([new BrowserCookie("SID", "secret-value", ".youtube.com", "/", true, true, false, null)], "Mozilla/5.0 AJ"));
        };
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private YtDlpDownloader Create() => new(
        Path.Combine(_root, "temp"),
        new YtDlpTools(() => _ytdlp, () => _ffmpeg, () => null),
        urls => _session(urls),
        (exe, args, onOutput, keep, token) =>
        {
            _calls.Add(args);
            return _behaviour(args, onOutput, token);
        });

    private string Target(string name) => Path.Combine(_root, "out", name);

    private static string After(IReadOnlyList<string> args, string option) => args[args.ToList().IndexOf(option) + 1];

    private static DownloadRequest Request(string target, string? video = "137", string? audio = "140", long? expected = null) =>
        new(Page, target, null, DownloadSource.YtDlp, null, video, audio, expected);

    [Fact]
    public async Task Downloads_with_progress_cookies_and_the_final_path()
    {
        string? cookieText = null;
        _behaviour = (args, onOutput, _) =>
        {
            if (!args.Contains("--cookies"))
            {
                // A public run first; this video wants a signed-in visitor.
                return Task.FromResult(new ProcessResult(1, [], ["ERROR: [youtube] abc: Sign in to confirm your age. This video may be inappropriate for some users."]));
            }

            cookieText = File.ReadAllText(After(args, "--cookies"));
            onOutput!("[youtube] abc123XYZ00: Downloading webpage");
            onOutput("ajb-progress 50 100 NA 1000 1 137");
            onOutput("ajb-progress 100 100 NA 1000 0 137");
            onOutput("ajb-progress 20 20 NA 500 0 140");
            onOutput("[Merger] Merging formats into \"x\"");
            var final = Target("My video 1080p.webm");
            File.WriteAllBytes(final, new byte[120]);
            onOutput("ajb-file:" + final);
            return Task.FromResult(new ProcessResult(0, [], []));
        };
        var reports = new List<DownloadProgress>();

        var result = await Create().DownloadAsync(5, Request(Target("My video 1080p.webm")), new SyncProgress(reports.Add), CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.Null(result.FinalPath); // the planned name
        Assert.Equal(120, result.ReceivedBytes);
        Assert.Equal(2, _calls.Count);
        Assert.DoesNotContain("--cookies", _calls[0]); // cookies only once the site asked for sign-in
        Assert.DoesNotContain("--user-agent", _calls[0]);
        var args = _calls[1];
        Assert.Equal("137+140", After(args, "-f"));
        Assert.Equal("webm", After(args, "--merge-output-format")); // from the target's extension
        Assert.Equal(@"C:\tools\ffmpeg.exe", After(args, "--ffmpeg-location"));
        Assert.Equal("Mozilla/5.0 AJ", After(args, "--user-agent"));
        Assert.Equal("My video 1080p.%(ext)s", After(args, "-o"));
        Assert.Contains("SID\tsecret-value", cookieText);
        Assert.DoesNotContain(args, a => a.Contains("secret-value", StringComparison.Ordinal)); // never on the command line
        Assert.False(Directory.Exists(Path.Combine(_root, "temp", "5"))); // temp folder (and cookie file) gone
        Assert.Contains(reports, r => r.ReceivedBytes == 120 && r.TotalBytes == 120);
        Assert.Contains("https://accounts.google.com/", _sessionUrls!.Select(u => u.AbsoluteUri)); // YouTube signs in through Google
    }

    [Fact]
    public async Task The_expected_size_is_reported_while_the_sound_part_is_still_unknown()
    {
        var reports = new List<DownloadProgress>();
        _behaviour = (_, onOutput, _) =>
        {
            onOutput!("ajb-progress 1000 300000 NA 1000 9 137"); // picture running, sound size unknown
            File.WriteAllText(Target("e.mp4"), "x");
            onOutput("ajb-file:" + Target("e.mp4"));
            return Task.FromResult(new ProcessResult(0, [], []));
        };
        await Create().DownloadAsync(1, Request(Target("e.mp4"), expected: 400_000), new SyncProgress(reports.Add), CancellationToken.None);
        Assert.Equal((1000L, 400_000L), (reports[0].ReceivedBytes, reports[0].TotalBytes!.Value));
    }

    [Fact]
    public async Task A_garbled_final_path_falls_back_to_the_planned_name()
    {
        // Before --encoding utf-8, yt-dlp printed an Urdu path with the Urdu letters missing (your report, F23).
        const string urdu = "آج بڑے سے بڑا عالم دین؟ 1080p";
        _behaviour = (_, onOutput, _) =>
        {
            File.WriteAllText(Target(urdu + ".mp4"), "video");
            onOutput!("ajb-file:" + Target(" 1080p.mp4")); // what arrived: the letters gone
            return Task.FromResult(new ProcessResult(0, [], []));
        };
        var result = await Create().DownloadAsync(1, Request(Target(urdu + ".mp4")), null, CancellationToken.None);
        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.Null(result.FinalPath); // the planned file
    }

    [Fact]
    public void Finding_the_output_ignores_yt_dlps_leftovers()
    {
        File.WriteAllText(Target("Clip 720p.f137.mp4.part"), "x");
        File.WriteAllText(Target("Clip 720p.part"), "x");
        Assert.Null(YtDlpDownloader.FindOutput(Target("Clip 720p.mp4")));
        File.WriteAllText(Target("Clip 720p.mkv"), "x"); // yt-dlp chose another container
        Assert.Equal(Target("Clip 720p.mkv"), YtDlpDownloader.FindOutput(Target("Clip 720p.mp4")));
    }

    [Fact]
    public async Task A_different_final_name_is_reported()
    {
        _behaviour = (_, onOutput, _) =>
        {
            var final = Target("Clip.mkv");
            File.WriteAllText(final, "x");
            onOutput!("ajb-file:" + final);
            return Task.FromResult(new ProcessResult(0, [], []));
        };
        var result = await Create().DownloadAsync(1, Request(Target("Clip.mp4"), "18", null), null, CancellationToken.None);
        Assert.Equal(Target("Clip.mkv"), result.FinalPath);
        Assert.DoesNotContain("--merge-output-format", _calls.Single()); // one format: nothing to merge
    }

    [Fact]
    public async Task Pause_stops_the_process_and_keeps_the_temp_folder()
    {
        _behaviour = async (args, onOutput, token) =>
        {
            onOutput!("ajb-progress 30 100 NA 1000 1 137");
            await Task.Delay(Timeout.Infinite, token);
            return new ProcessResult(0, [], []);
        };
        using var cts = new CancellationTokenSource();
        var running = Create().DownloadAsync(9, Request(Target("v.mp4")), null, cts.Token);
        await Task.Delay(100);
        cts.Cancel();
        var result = await running;

        Assert.Equal(DownloadOutcome.Cancelled, result.Outcome);
        Assert.Equal(30, result.ReceivedBytes);
        Assert.True(Directory.Exists(Path.Combine(_root, "temp", "9"))); // yt-dlp's .part files stay for Resume
        Assert.False(File.Exists(Path.Combine(_root, "temp", "9", "cookies.txt"))); // but never the cookies

        var downloader = Create();
        downloader.DeleteTemp(9); // Cancel
        Assert.False(Directory.Exists(Path.Combine(_root, "temp", "9")));
    }

    [Fact]
    public async Task Failures_are_explained()
    {
        _behaviour = (_, _, _) => Task.FromResult(new ProcessResult(1, [], ["ERROR: [youtube] abc: Sign in to confirm your age. This video may be inappropriate for some users."]));
        var result = await Create().DownloadAsync(2, Request(Target("v.mp4")), null, CancellationToken.None);
        Assert.Equal(DownloadOutcome.Failed, result.Outcome);
        Assert.StartsWith("The site still refused, although AJBrowser sent your sign-in", result.Error, StringComparison.Ordinal); // retried with cookies, still refused

        _behaviour = (_, _, _) => Task.FromResult(new ProcessResult(-1, [], ["couldn't be started"]));
        Assert.Contains("antivirus", (await Create().DownloadAsync(3, Request(Target("w.mp4")), null, CancellationToken.None)).Error);

        _behaviour = (_, _, _) => Task.FromResult(new ProcessResult(0, [], []));
        Assert.Contains("file wasn't found", (await Create().DownloadAsync(4, Request(Target("x.mp4")), null, CancellationToken.None)).Error);
    }

    [Fact]
    public async Task Missing_tools_fail_before_running_anything()
    {
        _ffmpeg = null;
        Assert.Contains("needs FFmpeg", (await Create().DownloadAsync(1, Request(Target("v.mp4")), null, CancellationToken.None)).Error);
        Assert.Equal(DownloadOutcome.Failed, (await Create().DownloadAsync(1, Request(Target("v.mp4"), null, null), null, CancellationToken.None)).Outcome);

        _ytdlp = null;
        Assert.Contains("isn't installed", (await Create().DownloadAsync(1, Request(Target("v.mp4"), "18", null), null, CancellationToken.None)).Error);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task Without_a_browser_session_it_runs_without_cookies()
    {
        _session = _ => throw new InvalidOperationException("no WebView yet");
        _behaviour = (_, onOutput, _) =>
        {
            File.WriteAllText(Target("v.mp4"), "x");
            onOutput!("ajb-file:" + Target("v.mp4"));
            return Task.FromResult(new ProcessResult(0, [], []));
        };
        var result = await Create().DownloadAsync(1, Request(Target("v.mp4"), "18", null), null, CancellationToken.None);
        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.DoesNotContain("--cookies", _calls.Single());
    }

    [Fact]
    public async Task Inspect_parses_and_caches_and_cleans_up()
    {
        _behaviour = (args, _, _) =>
        {
            Assert.DoesNotContain("--cookies", args); // a public page needs no cookies
            return Task.FromResult(new ProcessResult(0, [YtDlpFixtures.YouTube()], []));
        };
        var downloader = Create();
        var first = await downloader.InspectAsync(Page);
        var second = await downloader.InspectAsync(Page);

        Assert.Null(first.Problem);
        Assert.Equal("My \"great\" video", first.Info!.Title);
        Assert.Same(first.Info, second.Info);
        Assert.Single(_calls); // cached
        Assert.Contains("-J", _calls[0]);
        var temp = Path.Combine(_root, "temp");
        Assert.True(!Directory.Exists(temp) || Directory.GetDirectories(temp).Length == 0); // no inspection folder left

        downloader.Forget(Page);
        await downloader.InspectAsync(Page);
        Assert.Equal(2, _calls.Count);
    }

    [Fact]
    public async Task A_sign_in_answer_retries_with_the_browsers_cookies_and_remembers_it()
    {
        _behaviour = (args, onOutput, _) =>
        {
            if (!args.Contains("--cookies"))
            {
                return Task.FromResult(new ProcessResult(1, [], ["ERROR: [instagram] X: Requested content is not available, rate-limit reached or login required. Use --cookies"]));
            }

            Assert.True(File.Exists(After(args, "--cookies")));
            Assert.Equal("Mozilla/5.0 AJ", After(args, "--user-agent"));
            if (args.Contains("-J"))
            {
                return Task.FromResult(new ProcessResult(0, [YtDlpFixtures.YouTube()], []));
            }

            File.WriteAllText(Target("reel.mp4"), "x");
            onOutput!("ajb-file:" + Target("reel.mp4"));
            return Task.FromResult(new ProcessResult(0, [], []));
        };
        var downloader = Create();

        var inspection = await downloader.InspectAsync(Page);
        Assert.NotNull(inspection.Info);
        Assert.Equal(2, _calls.Count); // public try, then as the browser
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "temp"))); // no cookie file left behind

        var result = await downloader.DownloadAsync(3, Request(Target("reel.mp4"), "18", null), null, CancellationToken.None);
        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.Equal(3, _calls.Count); // the download went straight to cookies (remembered)
        Assert.Contains("--cookies", _calls[2]);
    }

    [Fact]
    public async Task Signed_in_but_still_refused_says_so_and_keeps_yt_dlps_words_for_the_log()
    {
        _behaviour = (args, _, _) => Task.FromResult(new ProcessResult(1, [],
        [
            "WARNING: [youtube] The provided YouTube account cookies are no longer valid. They have likely been rotated in the browser as a security measure.",
            "ERROR: [youtube] abc: Sign in to confirm your age. This video may be inappropriate for some users.",
        ]));
        var problem = (await Create().InspectAsync(Page)).Problem!;
        Assert.Equal(2, _calls.Count);
        Assert.StartsWith("The site still refused, although AJBrowser sent your sign-in", problem.Message, StringComparison.Ordinal);
        Assert.StartsWith("with 1 browser cookies: WARNING: [youtube] The provided YouTube account cookies are no longer valid", problem.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", problem.Detail);
    }

    [Fact]
    public async Task Not_signed_in_in_ajbrowser_says_so()
    {
        _session = _ => Task.FromResult(new BrowserSession([], "UA"));
        _behaviour = (_, _, _) => Task.FromResult(new ProcessResult(1, [], ["ERROR: [youtube] abc: Sign in to confirm your age. This video may be inappropriate for some users."]));
        var problem = (await Create().InspectAsync(Page)).Problem!;
        Assert.StartsWith("You don't seem to be signed in to this site in AJBrowser", problem.Message, StringComparison.Ordinal);
        Assert.Equal(YtDlpFix.SignIn, problem.Fix);
    }

    [Fact]
    public async Task Without_sign_in_trouble_cookies_are_never_read()
    {
        var sessionCalls = 0;
        _session = _ => { sessionCalls++; return Task.FromResult(BrowserSession.Empty); };
        _behaviour = (_, _, _) => Task.FromResult(new ProcessResult(0, [YtDlpFixtures.YouTube()], []));
        await Create().InspectAsync(Page);
        Assert.Equal(0, sessionCalls);
    }

    [Fact]
    public async Task Inspect_reports_problems_instead_of_throwing()
    {
        _behaviour = (_, _, _) => Task.FromResult(new ProcessResult(1, [], ["ERROR: [generic] Unsupported URL: https://example.com/"]));
        var unsupported = await Create().InspectAsync(new Uri("https://example.com/"));
        Assert.Null(unsupported.Info);
        Assert.StartsWith("yt-dlp doesn't support this page", unsupported.Problem!.Message, StringComparison.Ordinal);

        _behaviour = (_, _, _) => Task.FromResult(new ProcessResult(0, ["not json"], []));
        Assert.Equal(YtDlpFix.UpdateYtDlp, (await Create().InspectAsync(Page)).Problem!.Fix);

        _ytdlp = null;
        Assert.Contains("isn't installed", (await Create().InspectAsync(Page)).Problem!.Message);
    }

    [Theory]
    [InlineData(@"C:\d\a.webm", "webm")]
    [InlineData(@"C:\d\a.MKV", "mkv")]
    [InlineData(@"C:\d\a.mp4", "mp4")]
    [InlineData(@"C:\d\a.m4a", "mp4")]
    public void Merge_format_follows_the_target(string target, string expected) =>
        Assert.Equal(expected, YtDlpDownloader.MergeFormatFor(target));

    [Fact]
    public async Task The_router_sends_yt_dlp_jobs_to_yt_dlp()
    {
        var hls = new HlsDownloader(new HttpClient(new FakeHlsServer()), Path.Combine(_root, "hls"), () => null);
        var router = new StreamDownloaders(hls, new DashDownloader(hls, () => null), Create());
        _behaviour = (_, onOutput, _) =>
        {
            File.WriteAllText(Target("r.mp4"), "x");
            onOutput!("ajb-file:" + Target("r.mp4"));
            return Task.FromResult(new ProcessResult(0, [], []));
        };
        var result = await router.DownloadAsync(1, Request(Target("r.mp4"), "18", null), null, CancellationToken.None);
        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.Single(_calls);

        var noYtDlp = new StreamDownloaders(hls, new DashDownloader(hls, () => null));
        Assert.Equal(DownloadOutcome.Failed, (await noYtDlp.DownloadAsync(2, Request(Target("s.mp4"), "18", null), null, CancellationToken.None)).Outcome);
    }

    [Fact]
    public void Real_processes_join_the_kill_on_close_job()
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false })!;
        Assert.True(ChildProcessJob.TryAssign(process));
        process.Kill(entireProcessTree: true);
    }

    [Fact]
    public void Settings_defaults_and_normalizing()
    {
        var defaults = new AppSettings();
        Assert.False(defaults.YtDlpEnabled); // off until the notice is accepted
        Assert.True(defaults.AutoUpdateYtDlp);
        var normalized = (defaults with { YtDlpPath = "  ", DenoPath = "" }).Normalize();
        Assert.Null(normalized.YtDlpPath);
        Assert.Null(normalized.DenoPath);
    }

    private sealed class SyncProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }
}
