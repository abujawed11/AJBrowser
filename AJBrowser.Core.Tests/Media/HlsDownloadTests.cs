using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using AJBrowser.Core.Downloads;
using AJBrowser.Core.Media.Ffmpeg;
using AJBrowser.Core.Media.Hls;
using AJBrowser.Core.Persistence;
using AJBrowser.Core.Tests.Persistence;

namespace AJBrowser.Core.Tests.Media;

/// <summary>An in-memory web server for HLS tests: URL → bytes, byte ranges, scripted failures, request log.</summary>
internal sealed class FakeHlsServer : HttpMessageHandler
{
    public ConcurrentDictionary<string, byte[]> Files { get; } = new();
    public ConcurrentDictionary<string, ConcurrentQueue<HttpStatusCode>> Failures { get; } = new();
    public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

    /// <summary>When set, requests for this URL wait until the token is cancelled (to test pausing mid-download).</summary>
    public string? HangOn { get; set; }

    public void Add(string url, string text) => Files[url] = Encoding.UTF8.GetBytes(text);

    public void FailNext(string url, params HttpStatusCode[] statuses) =>
        Failures[url] = new ConcurrentQueue<HttpStatusCode>(statuses);

    public int CountFor(string url) => Requests.Count(r => r.RequestUri!.AbsoluteUri == url);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        var url = request.RequestUri!.AbsoluteUri;
        if (url == HangOn)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        if (Failures.TryGetValue(url, out var queue) && queue.TryDequeue(out var status))
        {
            return new HttpResponseMessage(status);
        }

        if (!Files.TryGetValue(url, out var data))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (request.Headers.Range?.Ranges.FirstOrDefault() is { From: { } from, To: { } to })
        {
            var slice = data.AsSpan((int)from, (int)(to - from + 1)).ToArray();
            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(slice) };
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
    }
}

public sealed class HlsDownloaderTests : IDisposable
{
    private const string Base = "https://cdn.example/show/";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AJBrowserTests", Guid.NewGuid().ToString("N"));
    private readonly FakeHlsServer _server = new();

    public HlsDownloaderTests() => Directory.CreateDirectory(Path.Combine(_root, "out"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private HlsDownloader Create(string? ffmpeg = null) =>
        new(new HttpClient(_server), Path.Combine(_root, "temp"), () => ffmpeg, (_, _) => Task.CompletedTask) { Parallelism = 3 };

    private string Target(string name) => Path.Combine(_root, "out", name);

    private static byte[] Bytes(int length, byte seed) => Enumerable.Range(0, length).Select(i => (byte)(seed + i)).ToArray();

    /// <summary>A VOD TS playlist with n segments of distinct bytes; returns the expected joined content.</summary>
    private byte[] TsStream(int count, string host = "cdn.example", string playlist = "prog.m3u8")
    {
        var text = new StringBuilder("#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXT-X-PLAYLIST-TYPE:VOD\n");
        var all = new List<byte>();
        for (var i = 0; i < count; i++)
        {
            var url = $"https://{host}/show/seg{i}.ts";
            var data = Bytes(1000 + i, (byte)(i * 7));
            _server.Files[url] = data;
            all.AddRange(data);
            text.Append($"#EXTINF:4,\n{url}\n");
        }

        text.Append("#EXT-X-ENDLIST\n");
        _server.Add(Base + playlist, text.ToString());
        return [.. all];
    }

    private static DownloadRequest Request(string target, string playlist = Base + "prog.m3u8", Dictionary<string, string>? headers = null) =>
        new(new Uri(playlist), target, headers, DownloadSource.Hls);

    [Fact]
    public async Task Joins_ts_segments_in_order()
    {
        var expected = TsStream(7);
        var reports = new List<DownloadProgress>();

        var result = await Create().DownloadAsync(1, Request(Target("show.ts")), new SyncProgress(reports.Add), CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.Equal(expected, File.ReadAllBytes(Target("show.ts")));
        Assert.False(Directory.Exists(Path.Combine(_root, "temp", "1"))); // temp pieces removed
        Assert.Equal(expected.Length, reports[^1].ReceivedBytes);
        Assert.Equal(expected.Length, result.ReceivedBytes);
    }

    [Fact]
    public async Task Without_ffmpeg_a_ts_stream_meant_as_mp4_is_saved_as_ts()
    {
        var expected = TsStream(3);
        var result = await Create(ffmpeg: null).DownloadAsync(2, Request(Target("show.mp4")), null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.Equal(Target("show.ts"), result.FinalPath);
        Assert.Equal(expected, File.ReadAllBytes(Target("show.ts")));
        Assert.False(File.Exists(Target("show.mp4")));
    }

    [Fact]
    public async Task Fmp4_with_init_section_and_byte_ranges_becomes_one_file()
    {
        var main = Bytes(6000, 3);
        _server.Files[Base + "main.mp4"] = main;
        _server.Add(Base + "f.m3u8", """
            #EXTM3U
            #EXT-X-TARGETDURATION:6
            #EXT-X-MAP:URI="main.mp4",BYTERANGE="500@0"
            #EXTINF:6,
            #EXT-X-BYTERANGE:2000@500
            main.mp4
            #EXTINF:6,
            #EXT-X-BYTERANGE:3500
            main.mp4
            #EXT-X-ENDLIST
            """);

        var result = await Create().DownloadAsync(3, Request(Target("f.mp4"), Base + "f.m3u8"), null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.Equal(main, File.ReadAllBytes(Target("f.mp4"))); // init 0–499, then 500–2499, then 2500–5999
    }

    [Fact]
    public async Task Decrypts_aes128_segments_and_fetches_the_key_once()
    {
        var key = Bytes(16, 40);
        _server.Files["https://keys.example/k"] = key;
        var plain = new List<byte>();
        var text = new StringBuilder("#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:5\n#EXT-X-KEY:METHOD=AES-128,URI=\"https://keys.example/k\"\n");
        using var aes = Aes.Create();
        aes.Key = key;
        for (var i = 0; i < 4; i++)
        {
            var data = Bytes(777 + i, (byte)i);
            plain.AddRange(data);
            var iv = HlsDecryptor.IvFor(new HlsSegment(new Uri(Base + "x"), 4, 5 + i, null, null, null, false));
            _server.Files[$"{Base}e{i}.ts"] = aes.EncryptCbc(data, iv, PaddingMode.PKCS7);
            text.Append($"#EXTINF:4,\ne{i}.ts\n");
        }

        _server.Add(Base + "enc.m3u8", text.Append("#EXT-X-ENDLIST\n").ToString());

        var result = await Create().DownloadAsync(4, Request(Target("enc.ts"), Base + "enc.m3u8"), null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.Equal(plain.ToArray(), File.ReadAllBytes(Target("enc.ts")));
        Assert.Equal(1, _server.CountFor("https://keys.example/k"));
    }

    [Fact]
    public async Task Retries_a_failing_segment()
    {
        var expected = TsStream(3);
        _server.FailNext("https://cdn.example/show/seg1.ts", HttpStatusCode.ServiceUnavailable, HttpStatusCode.BadGateway);

        var result = await Create().DownloadAsync(5, Request(Target("r.ts")), null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.Equal(expected, File.ReadAllBytes(Target("r.ts")));
        Assert.Equal(3, _server.CountFor("https://cdn.example/show/seg1.ts"));
    }

    [Fact]
    public async Task An_expired_segment_fails_clearly_and_retry_fetches_only_what_is_missing()
    {
        var expected = TsStream(6);
        _server.FailNext("https://cdn.example/show/seg4.ts", HttpStatusCode.Forbidden);
        var downloader = new HlsDownloader(new HttpClient(_server), Path.Combine(_root, "temp"), () => null, (_, _) => Task.CompletedTask) { Parallelism = 1 };

        var first = await downloader.DownloadAsync(6, Request(Target("x.ts")), null, CancellationToken.None);
        Assert.Equal(DownloadOutcome.Failed, first.Outcome);
        Assert.Contains("expired", first.Error);

        var second = await downloader.DownloadAsync(6, Request(Target("x.ts")), null, CancellationToken.None);
        Assert.Equal(DownloadOutcome.Completed, second.Outcome);
        Assert.Equal(expected, File.ReadAllBytes(Target("x.ts")));
        Assert.Equal(1, _server.CountFor("https://cdn.example/show/seg0.ts")); // kept from the first run
        Assert.Equal(2, _server.CountFor("https://cdn.example/show/seg4.ts"));
    }

    [Fact]
    public async Task Pausing_keeps_finished_pieces_and_cancel_deletes_them()
    {
        TsStream(5);
        _server.HangOn = "https://cdn.example/show/seg3.ts";
        var downloader = new HlsDownloader(new HttpClient(_server), Path.Combine(_root, "temp"), () => null, (_, _) => Task.CompletedTask) { Parallelism = 1 };
        using var cts = new CancellationTokenSource();
        var task = downloader.DownloadAsync(7, Request(Target("p.ts")), new SyncProgress(p => { if (p.ReceivedBytes > 2500) cts.Cancel(); }), cts.Token);

        var result = await task;
        Assert.Equal(DownloadOutcome.Cancelled, result.Outcome);
        Assert.True(result.ReceivedBytes > 0);
        Assert.True(Directory.GetFiles(downloader.TempFolderFor(7), "seg-*.bin").Length >= 2);

        downloader.DeleteTemp(7);
        Assert.False(Directory.Exists(downloader.TempFolderFor(7)));
    }

    [Fact]
    public async Task A_changed_playlist_starts_over()
    {
        TsStream(4);
        var downloader = Create();
        Directory.CreateDirectory(downloader.TempFolderFor(8));
        File.WriteAllText(Path.Combine(downloader.TempFolderFor(8), "job.json"), "{\"signature\":\"99:1.0\"}");
        File.WriteAllBytes(Path.Combine(downloader.TempFolderFor(8), "seg-000000.bin"), [1, 2, 3]); // stale piece

        var result = await downloader.DownloadAsync(8, Request(Target("c.ts")), null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.Equal(1, _server.CountFor("https://cdn.example/show/seg0.ts")); // fetched again, not reused
    }

    [Theory]
    [InlineData("#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://k\",KEYFORMAT=\"com.apple.streamingkeydelivery\"\n#EXTINF:4,\na.ts\n#EXT-X-ENDLIST\n", "DRM")]
    [InlineData("#EXTINF:4,\na.ts\n", "Live")]
    public async Task Refuses_protected_and_live_streams(string body, string expected)
    {
        _server.Add(Base + "bad.m3u8", "#EXTM3U\n#EXT-X-TARGETDURATION:4\n" + body);
        var result = await Create().DownloadAsync(9, Request(Target("bad.ts"), Base + "bad.m3u8"), null, CancellationToken.None);
        Assert.Equal(DownloadOutcome.Failed, result.Outcome);
        Assert.Contains(expected, result.Error);
    }

    [Fact]
    public async Task Sends_cookies_only_to_the_host_they_belong_to()
    {
        TsStream(2, host: "other-cdn.example");
        var headers = new Dictionary<string, string> { ["Cookie"] = "session=1", ["Referer"] = "https://site.example/" };

        var result = await Create().DownloadAsync(10, Request(Target("k.ts"), headers: headers), null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        var playlistRequest = _server.Requests.First(r => r.RequestUri!.Host == "cdn.example");
        Assert.True(playlistRequest.Headers.Contains("Cookie"));
        Assert.All(_server.Requests.Where(r => r.RequestUri!.Host == "other-cdn.example"), r =>
        {
            Assert.False(r.Headers.Contains("Cookie"));
            Assert.Equal("https://site.example/", r.Headers.Referrer!.AbsoluteUri);
        });
    }

    [Fact]
    public async Task Prepare_picks_the_best_quality_and_the_extension()
    {
        _server.Add(Base + "master.m3u8", """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="a",NAME="en",URI="audio.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360,CODECS="avc1.4d401e,mp4a.40.2"
            low.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080,CODECS="avc1.640028,mp4a.40.2",AUDIO="a"
            high.m3u8
            """);
        _server.Add(Base + "high.m3u8", "#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4,\nh0.ts\n#EXTINF:4,\nh1.ts\n#EXT-X-ENDLIST\n");
        _server.Add(Base + "low.m3u8", "#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4,\nl0.ts\n#EXT-X-ENDLIST\n");

        var plan = await Create(ffmpeg: null).PrepareAsync(new Uri(Base + "master.m3u8"), null);
        Assert.True(plan.CanDownload);
        Assert.Equal(Base + "high.m3u8", plan.MediaPlaylistUrl!.AbsoluteUri);
        Assert.Equal(".ts", plan.OutputExtension); // TS without FFmpeg
        Assert.True(plan.HasSeparateAudio);
        Assert.Equal(5_000_000L * 8 / 8, plan.EstimatedBytes);

        var withFfmpeg = await Create(ffmpeg: @"C:\tools\ffmpeg.exe").PrepareAsync(new Uri(Base + "master.m3u8"), null, new Uri(Base + "low.m3u8"));
        Assert.Equal(Base + "low.m3u8", withFfmpeg.MediaPlaylistUrl!.AbsoluteUri);
        Assert.Equal(".mp4", withFfmpeg.OutputExtension);
        Assert.False(withFfmpeg.HasSeparateAudio);
    }

    [Fact]
    public async Task Prepare_recognises_fmp4_and_protection()
    {
        _server.Add(Base + "f.m3u8", "#EXTM3U\n#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:4,\ns.m4s\n#EXT-X-ENDLIST\n");
        Assert.Equal(".mp4", (await Create().PrepareAsync(new Uri(Base + "f.m3u8"), null)).OutputExtension);

        _server.Add(Base + "drm.m3u8", "#EXTM3U\n#EXT-X-SESSION-KEY:METHOD=SAMPLE-AES,URI=\"skd://k\",KEYFORMAT=\"com.apple.streamingkeydelivery\"\n#EXT-X-STREAM-INF:BANDWIDTH=1\nv.m3u8\n");
        var drm = await Create().PrepareAsync(new Uri(Base + "drm.m3u8"), null);
        Assert.Equal(HlsSupport.Protected, drm.Support);
        Assert.False(drm.CanDownload);
    }

    private sealed class SyncProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value)
        {
            lock (this)
            {
                report(value);
            }
        }
    }
}

public sealed class HlsInDownloadManagerTests : DatabaseTestBase
{
    [Fact]
    public async Task A_stream_job_runs_through_the_manager_and_its_type_is_saved()
    {
        var server = new FakeHlsServer();
        server.Files["https://cdn.example/a.ts"] = [1, 2, 3];
        server.Files["https://cdn.example/b.ts"] = [4, 5];
        server.Add("https://cdn.example/p.m3u8", "#EXTM3U\n#EXTINF:4,\na.ts\n#EXTINF:4,\nb.ts\n#EXT-X-ENDLIST\n");
        var folder = Directory.CreateDirectory(Path.Combine(Root, "out")).FullName;
        var repo = new DownloadRepository(CreateInitialized());
        var hls = new HlsDownloader(new HttpClient(server), Path.Combine(Root, "temp"), () => null, (_, _) => Task.CompletedTask);
        var manager = new DownloadManager(repo, new HttpFileDownloader(new HttpClient(server)), new FileNamer(), streams: hls);

        var item = await manager.EnqueueAsync(new DownloadRequest(new Uri("https://cdn.example/p.m3u8"), Path.Combine(folder, "v.ts"), null, DownloadSource.Hls));
        for (var i = 0; i < 200 && manager.Find(item.Id)?.State != DownloadState.Completed; i++)
        {
            await Task.Delay(25);
        }

        var done = manager.Find(item.Id)!;
        Assert.Equal(DownloadState.Completed, done.State);
        Assert.Equal(5, done.TotalBytes); // the real size, not an estimate
        Assert.Equal([1, 2, 3, 4, 5], File.ReadAllBytes(Path.Combine(folder, "v.ts")));

        await manager.ShutdownAsync(TimeSpan.FromSeconds(2));
        var saved = Assert.Single(await repo.GetRecentAsync(10));
        Assert.Equal(DownloadSource.Hls, saved.Source);
    }
}

public sealed class FfmpegTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AJBrowserTests", Guid.NewGuid().ToString("N"));

    public FfmpegTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("out_time_us=30000000", 60.0, 0.5)]
    [InlineData("out_time_ms=15000000", 60.0, 0.25)]
    [InlineData("out_time=00:00:45.000000", 60.0, 0.75)]
    [InlineData("out_time_us=999000000", 60.0, 1.0)]
    [InlineData("progress=end", null, 1.0)]
    public void Reads_progress(string line, double? duration, double expected) =>
        Assert.Equal(expected, FfmpegProgress.TryParse(line, duration)!.Value, 3);

    [Theory]
    [InlineData("frame=100")]
    [InlineData("out_time_us=N/A")]
    [InlineData("")]
    public void Ignores_other_lines(string line) => Assert.Null(FfmpegProgress.TryParse(line, 60));

    [Fact]
    public void Remux_arguments_copy_streams_between_local_files()
    {
        var args = FfmpegRunner.RemuxArguments(@"C:\t\joined.ts", @"C:\o\v.mp4.ajpart", "MP4");
        Assert.Equal(["-hide_banner", "-nostdin", "-y", "-fflags", "+genpts", "-i", @"C:\t\joined.ts", "-map", "0:v?", "-map", "0:a?",
            "-c", "copy", "-movflags", "+faststart", "-f", "mp4", "-progress", "pipe:1", "-nostats", @"C:\o\v.mp4.ajpart"], args);
        Assert.DoesNotContain(args, a => a.Contains("http", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("matroska", FfmpegRunner.RemuxArguments("a", "b", "MKV"));
    }

    [Fact]
    public void Finds_ffmpeg_from_settings_or_path()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        var exe = Path.Combine(folder, FfmpegLocator.ExeName);
        File.WriteAllBytes(exe, []);

        Assert.Equal(exe, FfmpegLocator.Find(exe, pathVariable: ""));
        Assert.Equal(exe, FfmpegLocator.Find(folder, pathVariable: ""));                 // a folder works too
        Assert.Equal(exe, FfmpegLocator.Find(null, pathVariable: $"C:\\nope;{folder}")); // found on PATH
        Assert.Null(FfmpegLocator.Find(Path.Combine(_root, "missing.exe"), pathVariable: ""));
    }
}
