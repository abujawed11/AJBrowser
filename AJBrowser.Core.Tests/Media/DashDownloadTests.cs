using System.Net;
using AJBrowser.Core.Downloads;
using AJBrowser.Core.Media;
using AJBrowser.Core.Media.Dash;
using AJBrowser.Core.Media.Hls;
using AJBrowser.Core.Persistence;
using AJBrowser.Core.Tests.Persistence;

namespace AJBrowser.Core.Tests.Media;

public sealed class DashDownloaderTests : IDisposable
{
    private const string Base = "https://cdn.example/show/";
    private const string ManifestUrl = Base + "manifest.mpd";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AJBrowserTests", Guid.NewGuid().ToString("N"));
    private readonly FakeHlsServer _server = new();

    public DashDownloaderTests() => Directory.CreateDirectory(Path.Combine(_root, "out"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private HlsDownloader Engine(string? ffmpeg) =>
        new(new HttpClient(_server), Path.Combine(_root, "temp"), () => ffmpeg, (_, _) => Task.CompletedTask) { Parallelism = 3 };

    private DashDownloader Create(string? ffmpeg = null) => new(Engine(ffmpeg), () => ffmpeg);

    private string Target(string name) => Path.Combine(_root, "out", name);

    private static byte[] Bytes(int length, byte seed) => Enumerable.Range(0, length).Select(i => (byte)(seed + (i % 251))).ToArray();

    private static DownloadRequest Request(string target, string? video, string? audio = null, Dictionary<string, string>? headers = null) =>
        new(new Uri(ManifestUrl), target, headers, DownloadSource.Dash, null, video, audio);

    /// <summary>Serves a track's init section and n pieces under {id}/; returns the expected joined bytes.</summary>
    private byte[] ServeTrack(string id, int pieces, byte seed)
    {
        var all = new List<byte>();
        var init = Bytes(300, seed);
        _server.Files[$"{Base}{id}/init.mp4"] = init;
        all.AddRange(init);
        for (var i = 1; i <= pieces; i++)
        {
            var data = Bytes(1000 + i, (byte)(seed + i));
            _server.Files[$"{Base}{id}/{i}.m4s"] = data;
            all.AddRange(data);
        }

        return [.. all];
    }

    /// <summary>12 s at 4 s pieces: 1080p and 360p H.264 picture tracks, English and German AAC sound.</summary>
    private void ServeManifest(string videoCodec = "avc1.640028", string audioCodec = "mp4a.40.2", string extra = "")
    {
        _server.Add(ManifestUrl, $"""
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT12S">
              <Period>
                <AdaptationSet mimeType="video/mp4" codecs="{videoCodec}">
                  {extra}
                  <SegmentTemplate timescale="1" duration="4" initialization="$RepresentationID$/init.mp4" media="$RepresentationID$/$Number$.m4s" />
                  <Representation id="v1080" bandwidth="4000000" width="1920" height="1080" />
                  <Representation id="v360" bandwidth="600000" width="640" height="360" />
                </AdaptationSet>
                <AdaptationSet mimeType="audio/mp4" codecs="{audioCodec}" lang="en">
                  <SegmentTemplate timescale="1" duration="4" initialization="$RepresentationID$/init.mp4" media="$RepresentationID$/$Number$.m4s" />
                  <Representation id="en" bandwidth="128000" />
                </AdaptationSet>
                <AdaptationSet mimeType="audio/mp4" codecs="{audioCodec}" lang="de">
                  <SegmentTemplate timescale="1" duration="4" initialization="$RepresentationID$/init.mp4" media="$RepresentationID$/$Number$.m4s" />
                  <Representation id="de" bandwidth="192000" />
                </AdaptationSet>
              </Period>
            </MPD>
            """);
    }

    [Fact]
    public async Task Downloads_the_chosen_picture_track_into_one_mp4()
    {
        ServeManifest();
        var expected = ServeTrack("v360", 3, 10);

        var result = await Create().DownloadAsync(1, Request(Target("show.mp4"), "v360"), null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.Equal(expected, File.ReadAllBytes(Target("show.mp4")));
        Assert.False(Directory.Exists(Path.Combine(_root, "temp", "1")));
        Assert.Equal(0, _server.CountFor(Base + "v1080/1.m4s")); // only the chosen quality
        Assert.Equal(1, _server.CountFor(Base + "v360/init.mp4")); // init section fetched once
    }

    [Fact]
    public async Task Without_a_track_id_the_best_quality_is_used()
    {
        ServeManifest();
        var expected = ServeTrack("v1080", 3, 20);
        var result = await Create().DownloadAsync(2, Request(Target("best.mp4"), null), null, CancellationToken.None);
        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.Equal(expected, File.ReadAllBytes(Target("best.mp4")));
    }

    [Fact]
    public async Task Both_tracks_are_fetched_and_a_failed_merge_still_saves_the_picture()
    {
        ServeManifest();
        var video = ServeTrack("v360", 3, 30);
        ServeTrack("en", 3, 90);
        var missingFfmpeg = Path.Combine(_root, "no-such-ffmpeg.exe");

        var result = await Create(missingFfmpeg).DownloadAsync(3, Request(Target("merged.mkv"), "v360", "en"), null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.Equal(1, _server.CountFor(Base + "en/3.m4s"));
        Assert.Equal(Target("merged.mp4"), result.FinalPath); // the picture's own type, not the planned .mkv
        Assert.Equal(video, File.ReadAllBytes(Target("merged.mp4")));
    }

    [Fact]
    public async Task A_whole_file_track_is_fetched_in_ranged_chunks()
    {
        var file = Bytes((int)(DashDownloader.ChunkSize * 2) + 12_345, 7);
        _server.Files[Base + "movie.webm"] = file;
        _server.Add(ManifestUrl, """
            <MPD type="static" mediaPresentationDuration="PT30S"><Period>
              <AdaptationSet mimeType="video/webm" codecs="vp9"><Representation id="w" bandwidth="1" height="720">
                <BaseURL>movie.webm</BaseURL><SegmentBase indexRange="100-200" />
              </Representation></AdaptationSet>
            </Period></MPD>
            """);

        var result = await Create().DownloadAsync(4, Request(Target("movie.webm"), "w"), null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.Equal(file, File.ReadAllBytes(Target("movie.webm")));
        var ranges = _server.Requests.Where(r => r.RequestUri!.AbsoluteUri == Base + "movie.webm").Select(r => r.Headers.Range?.ToString()).ToList();
        Assert.Contains("bytes=0-0", ranges); // the size probe
        Assert.Equal(1 + 3, ranges.Count); // probe + 3 chunks
    }

    [Fact]
    public async Task A_whole_file_from_a_server_without_ranges_is_fetched_once()
    {
        _server.IgnoreRanges = true;
        var file = Bytes(50_000, 3);
        _server.Files[Base + "clip.mp4"] = file;
        _server.Add(ManifestUrl, """
            <MPD type="static" mediaPresentationDuration="PT5S"><Period>
              <AdaptationSet mimeType="video/mp4"><Representation id="c" bandwidth="1"><BaseURL>clip.mp4</BaseURL></Representation></AdaptationSet>
            </Period></MPD>
            """);

        var result = await Create().DownloadAsync(5, Request(Target("clip.mp4"), "c"), null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.Equal(file, File.ReadAllBytes(Target("clip.mp4")));
    }

    [Fact]
    public async Task Pausing_keeps_finished_pieces_and_resuming_fetches_only_the_rest()
    {
        ServeManifest();
        var expected = ServeTrack("v360", 3, 40);
        var downloader = Create();
        _server.HangOn = Base + "v360/3.m4s";
        using var cts = new CancellationTokenSource();
        var first = downloader.DownloadAsync(6, Request(Target("p.mp4"), "v360"), null, cts.Token);
        for (var i = 0; i < 200 && _server.CountFor(Base + "v360/3.m4s") == 0; i++)
        {
            await Task.Delay(10);
        }

        await Task.Delay(50); // let the other pieces finish
        cts.Cancel();
        Assert.Equal(DownloadOutcome.Cancelled, (await first).Outcome);

        _server.HangOn = null;
        var second = await downloader.DownloadAsync(6, Request(Target("p.mp4"), "v360"), null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, second.Outcome);
        Assert.Equal(expected, File.ReadAllBytes(Target("p.mp4")));
        Assert.Equal(1, _server.CountFor(Base + "v360/1.m4s")); // not fetched again
    }

    [Fact]
    public async Task A_track_that_disappeared_from_the_manifest_fails_clearly()
    {
        ServeManifest();
        var result = await Create().DownloadAsync(7, Request(Target("x.mp4"), "v4320"), null, CancellationToken.None);
        Assert.Equal(DownloadOutcome.Failed, result.Outcome);
        Assert.Contains("changed since the download started", result.Error);
    }

    [Fact]
    public async Task An_expired_manifest_link_says_so()
    {
        _server.FailNext(ManifestUrl, HttpStatusCode.Forbidden);
        var result = await Create().DownloadAsync(8, Request(Target("x.mp4"), "v360"), null, CancellationToken.None);
        Assert.Equal(DownloadOutcome.Failed, result.Outcome);
        Assert.Contains("HTTP 403", result.Error);
        Assert.DoesNotContain("cdn.example", result.Error);
    }

    [Fact]
    public async Task A_protected_manifest_is_refused_before_any_piece_is_fetched()
    {
        ServeManifest(extra: """<ContentProtection schemeIdUri="urn:mpeg:dash:mp4protection:2011" value="cenc" />""");
        ServeTrack("v360", 3, 1);
        var result = await Create().DownloadAsync(9, Request(Target("x.mp4"), "v360"), null, CancellationToken.None);
        Assert.Equal(DownloadOutcome.Failed, result.Outcome);
        Assert.Equal("Protected (DRM): can't be downloaded", result.Error);
        Assert.Equal(0, _server.CountFor(Base + "v360/1.m4s"));
    }

    [Fact]
    public async Task Cookies_go_only_to_the_manifests_host()
    {
        _server.Add(ManifestUrl, """
            <MPD type="static" mediaPresentationDuration="PT4S"><Period>
              <AdaptationSet mimeType="video/mp4"><Representation id="v" bandwidth="1">
                <SegmentTemplate duration="4" initialization="https://other-cdn.example/init.mp4" media="https://other-cdn.example/$Number$.m4s" />
              </Representation></AdaptationSet>
            </Period></MPD>
            """);
        _server.Files["https://other-cdn.example/init.mp4"] = [1];
        _server.Files["https://other-cdn.example/1.m4s"] = [2];
        var headers = new Dictionary<string, string> { ["Cookie"] = "session=secret", ["Referer"] = "https://site.example/" };

        var result = await Create().DownloadAsync(10, Request(Target("c.mp4"), "v", headers: headers), null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Completed, result.Outcome);
        Assert.True(_server.Requests.Single(r => r.RequestUri!.AbsoluteUri == ManifestUrl).Headers.Contains("Cookie"));
        var piece = _server.Requests.Single(r => r.RequestUri!.AbsoluteUri == "https://other-cdn.example/1.m4s");
        Assert.False(piece.Headers.Contains("Cookie"));
        Assert.True(piece.Headers.Contains("Referer"));
    }

    // ---------------------------------------------------------------- inspect / prepare

    [Fact]
    public async Task Inspect_lists_qualities_best_first_sound_tracks_and_length()
    {
        ServeManifest();
        var inspection = await Create().InspectAsync(new Uri(ManifestUrl), null);
        Assert.Equal(DashSupport.Supported, inspection.Support);
        Assert.Null(inspection.Problem);
        Assert.Equal(new[] { "v1080", "v360" }, inspection.Video.Select(v => v.Id));
        Assert.Equal(new[] { "en", "de" }, inspection.Audio.Select(a => a.Id));
        Assert.True(inspection.HasSeparateAudio);
        Assert.Equal(12, inspection.DurationSeconds);
        Assert.NotNull(inspection.Manifest); // the picker picks the sound per quality from it
    }

    [Fact]
    public async Task Inspect_never_throws_for_bad_manifests()
    {
        _server.Add(ManifestUrl, "<html>nope</html>");
        var bad = await Create().InspectAsync(new Uri(ManifestUrl), null);
        Assert.Equal(("The stream's manifest couldn't be read.", 0), (bad.Problem, bad.Video.Count));

        var missing = await Create().InspectAsync(new Uri(Base + "gone.mpd"), null);
        Assert.NotNull(missing.Problem);
    }

    [Fact]
    public async Task Prepare_plans_the_sound_only_when_ffmpeg_can_merge_it()
    {
        ServeManifest();
        var without = await Create(ffmpeg: null).PrepareAsync(new Uri(ManifestUrl), null, preferredLanguage: "en");
        Assert.True(without.CanDownload);
        Assert.Equal("v1080", without.Video!.Id);
        Assert.Null(without.Audio);
        Assert.True(without.HasSeparateAudio); // the picker warns: no sound
        Assert.Equal(".mp4", without.OutputExtension);

        var with = await Create(ffmpeg: @"C:\tools\ffmpeg.exe").PrepareAsync(new Uri(ManifestUrl), null, "v360", preferredLanguage: "de-AT");
        Assert.Equal(("v360", "de"), (with.Video!.Id, with.Audio!.Id));
        Assert.Equal(".mp4", with.OutputExtension);
        Assert.Equal((600_000L + 192_000) * 12 / 8, with.EstimatedBytes);

        var chosen = await Create(ffmpeg: @"C:\tools\ffmpeg.exe").PrepareAsync(new Uri(ManifestUrl), null, audioId: "en", preferredLanguage: "de");
        Assert.Equal("en", chosen.Audio!.Id);
    }

    [Fact]
    public async Task Prepare_picks_webm_for_vp9_and_opus()
    {
        ServeManifest(videoCodec: "vp09.00.40.08", audioCodec: "opus");
        var plan = await Create(ffmpeg: @"C:\tools\ffmpeg.exe").PrepareAsync(new Uri(ManifestUrl), null);
        Assert.Equal(".webm", plan.OutputExtension);
    }

    [Fact]
    public async Task Prepare_explains_refusals()
    {
        ServeManifest(extra: """<ContentProtection schemeIdUri="urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed" />""");
        var plan = await Create().PrepareAsync(new Uri(ManifestUrl), null);
        Assert.False(plan.CanDownload);
        Assert.Equal("Protected (DRM): can't be downloaded", plan.Problem);
    }

    [Theory]
    [InlineData("video/webm", ".webm")]
    [InlineData("video/mp4", ".mp4")]
    [InlineData("audio/mp4", ".mp4")]
    [InlineData("video/mp2t", ".ts")]
    [InlineData(null, ".mp4")]
    public void Track_file_type_follows_its_mime_type(string? mime, string expected) =>
        Assert.Equal(expected, DashDownloader.TrackExtension(new DashRepresentation(null, DashTrackKind.Video, mime, null, 0, null, null, null, null, null, null, false, false, null, [])));
}

public sealed class DashInDownloadManagerTests : DatabaseTestBase
{
    [Fact]
    public async Task A_dash_job_runs_through_the_router_and_its_tracks_are_saved()
    {
        var server = new FakeHlsServer();
        server.Add("https://cdn.example/m.mpd", """
            <MPD type="static" mediaPresentationDuration="PT8S"><Period>
              <AdaptationSet mimeType="video/mp4"><SegmentTemplate duration="4" media="$RepresentationID$-$Number$.m4s" />
                <Representation id="hi" bandwidth="2" /><Representation id="lo" bandwidth="1" />
              </AdaptationSet>
            </Period></MPD>
            """);
        server.Files["https://cdn.example/lo-1.m4s"] = [1, 2, 3];
        server.Files["https://cdn.example/lo-2.m4s"] = [4, 5];
        var folder = Directory.CreateDirectory(Path.Combine(Root, "out")).FullName;
        var repo = new DownloadRepository(CreateInitialized());
        var hls = new HlsDownloader(new HttpClient(server), Path.Combine(Root, "temp"), () => null, (_, _) => Task.CompletedTask);
        var streams = new StreamDownloaders(hls, new DashDownloader(hls, () => null));
        var manager = new DownloadManager(repo, new HttpFileDownloader(new HttpClient(server)), new FileNamer(), streams: streams);

        var item = await manager.EnqueueAsync(new DownloadRequest(new Uri("https://cdn.example/m.mpd"), Path.Combine(folder, "v.mp4"), null, DownloadSource.Dash, null, "lo", "en-audio"));
        for (var i = 0; i < 200 && manager.Find(item.Id)?.State is not (DownloadState.Completed or DownloadState.Failed); i++)
        {
            await Task.Delay(25);
        }

        // The audio id isn't in this manifest: the job fails clearly instead of saving a silent video.
        Assert.Equal(DownloadState.Failed, manager.Find(item.Id)!.State);

        var silent = await manager.EnqueueAsync(new DownloadRequest(new Uri("https://cdn.example/m.mpd"), Path.Combine(folder, "s.mp4"), null, DownloadSource.Dash, null, "lo"));
        for (var i = 0; i < 200 && manager.Find(silent.Id)?.State != DownloadState.Completed; i++)
        {
            await Task.Delay(25);
        }

        Assert.Equal(DownloadState.Completed, manager.Find(silent.Id)!.State);
        Assert.Equal([1, 2, 3, 4, 5], File.ReadAllBytes(Path.Combine(folder, "s.mp4")));

        await manager.ShutdownAsync(TimeSpan.FromSeconds(2));
        var saved = (await repo.GetRecentAsync(10)).Single(d => d.Id == item.Id);
        Assert.Equal((DownloadSource.Dash, "lo", "en-audio"), (saved.Source, saved.VideoTrackId, saved.AudioTrackId));
    }
}
