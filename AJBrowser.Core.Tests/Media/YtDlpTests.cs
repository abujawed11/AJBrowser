using AJBrowser.Core.Media.YtDlp;

namespace AJBrowser.Core.Tests.Media;

internal static class YtDlpFixtures
{
    private static string F(string id, string ext, string vcodec, string acodec, int? h = null, double? fps = null, double? tbr = null, long? size = null,
        long? approx = null, string? lang = null, int? langPref = null, string? note = null, string protocol = "https", string? drm = null, string? range = null)
    {
        var parts = new List<string>
        {
            $"\"format_id\":\"{id}\"", $"\"ext\":\"{ext}\"", $"\"vcodec\":\"{vcodec}\"", $"\"acodec\":\"{acodec}\"", $"\"protocol\":\"{protocol}\"",
            $"\"url\":\"https://rr1.googlevideo.example/videoplayback?id={id}&sig=SECRET\"",
        };
        if (h is { } height) { parts.Add($"\"height\":{height}"); parts.Add($"\"width\":{height * 16 / 9}"); }
        if (fps is { } f) { parts.Add($"\"fps\":{f.ToString(System.Globalization.CultureInfo.InvariantCulture)}"); }
        if (tbr is { } t) { parts.Add($"\"tbr\":{t.ToString(System.Globalization.CultureInfo.InvariantCulture)}"); }
        if (size is { } s) { parts.Add($"\"filesize\":{s}"); }
        if (approx is { } a) { parts.Add($"\"filesize_approx\":{a}"); }
        if (lang is not null) { parts.Add($"\"language\":\"{lang}\""); }
        if (langPref is { } lp) { parts.Add($"\"language_preference\":{lp}"); }
        if (note is not null) { parts.Add($"\"format_note\":\"{note}\""); }
        if (drm is not null) { parts.Add($"\"has_drm\":{drm}"); }
        if (range is not null) { parts.Add($"\"dynamic_range\":\"{range}\""); }
        return "{" + string.Join(",", parts) + "}";
    }

    /// <summary>A trimmed YouTube-style answer: 10:00 long, original English sound plus a German auto-dub.</summary>
    public static string YouTube(string? extra = null, string liveStatus = "not_live") => "{" + $$"""
        "id":"abc123XYZ00","title":"My \"great\" video","duration":600,"uploader":"Some Channel","extractor_key":"Youtube",
        "webpage_url":"https://www.youtube.com/watch?v=abc123XYZ00","thumbnail":"https://i.ytimg.example/vi/abc/maxres.jpg",
        "live_status":"{{liveStatus}}",{{extra}}
        "formats":[
        """ + string.Join(",",
        F("sb0", "mhtml", "none", "none", h: 90, protocol: "mhtml", note: "storyboard"),
        F("139", "m4a", "none", "mp4a.40.5", tbr: 48.8, size: 3_600_000, lang: "en", langPref: 10, note: "low, original (default)"),
        F("140", "m4a", "none", "mp4a.40.2", tbr: 129.5, size: 9_700_000, lang: "en", langPref: 10, note: "medium, original (default)"),
        F("251", "webm", "none", "opus", tbr: 135.2, size: 9_400_000, lang: "en", langPref: 10, note: "medium, original (default)"),
        F("251-drc", "webm", "none", "opus", tbr: 136.0, size: 9_450_000, lang: "en", langPref: 10, note: "medium, original (default), DRC"),
        F("140-de", "m4a", "none", "mp4a.40.2", tbr: 129.5, size: 9_700_000, lang: "de", langPref: -1, note: "German - dubbed-auto"),
        F("251-de", "webm", "none", "opus", tbr: 133.0, size: 9_300_000, lang: "de", langPref: -1, note: "German - dubbed-auto"),
        F("160", "mp4", "avc1.4d400c", "none", h: 144, fps: 30, tbr: 80, size: 5_000_000),
        F("278", "webm", "vp9", "none", h: 144, fps: 30, tbr: 70, size: 4_500_000),
        F("134", "mp4", "avc1.4d401e", "none", h: 360, fps: 30, tbr: 400, size: 30_000_000),
        F("18", "mp4", "avc1.42001E", "mp4a.40.2", h: 360, fps: 30, tbr: 500, approx: 37_500_000),
        F("136", "mp4", "avc1.4d401f", "none", h: 720, fps: 30, tbr: 1500, size: 110_000_000),
        F("247", "webm", "vp9", "none", h: 720, fps: 30, tbr: 1400, size: 100_000_000),
        F("298", "mp4", "avc1.4d4020", "none", h: 720, fps: 60, tbr: 2500, size: 180_000_000),
        F("137", "mp4", "avc1.640028", "none", h: 1080, fps: 30, tbr: 4000, size: 290_000_000),
        F("399", "mp4", "av01.0.08M.08", "none", h: 1080, fps: 30, tbr: 2800, size: 200_000_000),
        F("299", "mp4", "avc1.64002a", "none", h: 1080, fps: 60, tbr: 6000, size: 430_000_000),
        F("271", "webm", "vp9", "none", h: 1440, fps: 30, tbr: 9000, approx: 650_000_000, range: "HDR10"),
        F("drm1", "mp4", "avc1.640028", "none", h: 2160, fps: 30, tbr: 20000, drm: "\"maybe\"")) + "]}";
}

public sealed class YtDlpInfoParserTests
{
    [Fact]
    public void Reads_video_fields_and_formats()
    {
        var info = YtDlpInfoParser.Parse(YtDlpFixtures.YouTube());
        Assert.Equal(("abc123XYZ00", "My \"great\" video", 600.0, "Some Channel", "Youtube"), (info.Id, info.Title, info.DurationSeconds!.Value, info.Uploader, info.Extractor));
        Assert.Equal("https://www.youtube.com/watch?v=abc123XYZ00", info.WebpageUrl!.AbsoluteUri);
        Assert.Equal(YtDlpLiveStatus.NotLive, info.LiveStatus);
        Assert.False(info.IsPlaylist);
        Assert.Equal(19, info.Formats.Count);

        var hd = info.Formats.Single(f => f.FormatId == "299");
        Assert.Equal(("avc1.64002a", null, 1080, 60.0, 430_000_000L), (hd.VideoCodec, hd.AudioCodec, hd.Height!.Value, hd.Fps!.Value, hd.FileSize!.Value));
        Assert.True(hd.IsVideoOnly);
        Assert.True(info.Formats.Single(f => f.FormatId == "140").IsAudioOnly);
        Assert.True(info.Formats.Single(f => f.FormatId == "18").IsMuxed);
        Assert.True(info.Formats.Single(f => f.FormatId == "sb0").IsJunk);
        Assert.True(info.Formats.Single(f => f.FormatId == "drm1").HasDrm); // "maybe" counts as protected
        Assert.Equal((10, "en"), (info.Formats.Single(f => f.FormatId == "140").LanguagePreference!.Value, info.Formats.Single(f => f.FormatId == "140").Language));
    }

    [Theory]
    [InlineData("is_live", YtDlpLiveStatus.Live)]
    [InlineData("is_upcoming", YtDlpLiveStatus.Upcoming)]
    [InlineData("post_live", YtDlpLiveStatus.PostLive)]
    [InlineData("was_live", YtDlpLiveStatus.NotLive)]
    public void Reads_live_status(string status, YtDlpLiveStatus expected) =>
        Assert.Equal(expected, YtDlpInfoParser.Parse(YtDlpFixtures.YouTube(liveStatus: status)).LiveStatus);

    [Fact]
    public void A_single_file_site_reports_its_format_at_the_top_level()
    {
        var info = YtDlpInfoParser.Parse("""{"title":"Clip","format_id":"hd","ext":"mp4","vcodec":"h264","acodec":"aac","height":720,"url":"https://x.example/a.mp4"}""");
        Assert.Equal("hd", Assert.Single(info.Formats).FormatId);
    }

    [Fact]
    public void Untrusted_values_are_checked_and_cleaned()
    {
        var info = YtDlpInfoParser.Parse("""{"title":"Bad\u0007title\n","duration":-5,"webpage_url":"javascript:alert(1)","thumbnail":"file:///c:/x","formats":[{"format_id":"a","height":"tall","fps":-1,"filesize":"big","vcodec":"none","acodec":"none"},{"ext":"mp4"}]}""");
        Assert.Equal("Badtitle", info.Title);
        Assert.Null(info.DurationSeconds);
        Assert.Null(info.WebpageUrl);
        Assert.Null(info.Thumbnail);
        var only = Assert.Single(info.Formats); // the one without format_id is dropped
        Assert.Equal((null, null, null), (only.Height, only.Fps, only.FileSize));
        Assert.True(only.IsJunk);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("")]
    public void Rejects_what_is_not_video_info(string text) => Assert.Throws<FormatException>(() => YtDlpInfoParser.Parse(text));

    [Fact]
    public void Output_parsing_takes_the_json_line()
    {
        var info = YtDlpInfoParser.ParseOutput(["[youtube] Extracting URL", """{"title":"T","formats":[]}"""]);
        Assert.Equal("T", info.Title);
        Assert.Throws<FormatException>(() => YtDlpInfoParser.ParseOutput(["nothing here"]));
    }

    [Fact]
    public void Playlists_are_recognised()
    {
        Assert.True(YtDlpInfoParser.Parse("""{"_type":"playlist","title":"Mix","entries":[]}""").IsPlaylist);
    }
}

public sealed class YtDlpFormatsTests
{
    private static readonly YtDlpInfo Info = YtDlpInfoParser.Parse(YtDlpFixtures.YouTube());

    [Fact]
    public void With_ffmpeg_one_row_per_height_and_frame_rate_best_first()
    {
        var choices = YtDlpFormats.Build(Info, ffmpegAvailable: true, "en-US");
        Assert.True(choices.CanDownload);
        Assert.Null(choices.Note);
        var video = choices.Rows.Where(r => !r.IsAudioOnly).Select(r => r.FormatSelector).ToList();
        Assert.Equal(new[] { "271+251", "299+140", "137+140", "298+140", "136+140", "134+140", "160+140" }, video);
        Assert.DoesNotContain(choices.Rows, r => r.FormatSelector.StartsWith("drm1", StringComparison.Ordinal)); // DRM never offered
    }

    [Fact]
    public void Prefers_h264_then_av1_then_vp9_and_pairs_matching_sound()
    {
        var rows = YtDlpFormats.Build(Info, true, "en").Rows;
        var p1080 = rows.Single(r => r.FormatSelector == "137+140");
        Assert.Equal(("mp4", "mp4", 299_700_000L, false), (p1080.MergeFormat, p1080.OutputExtension, p1080.EstimatedBytes!.Value, p1080.IsEstimate));
        Assert.Equal("1080p · H.264 · MP4 · 286 MB", p1080.Label);

        var p1440 = rows.Single(r => r.Height == 1440); // only VP9 there: Opus sound, WebM
        Assert.Equal(("271+251", "webm"), (p1440.FormatSelector, p1440.MergeFormat));
        Assert.True(p1440.IsEstimate);
        Assert.StartsWith("1440p · HDR · VP9 · WEBM · ≈ ", p1440.Label, StringComparison.Ordinal);

        Assert.StartsWith("1080p · 60fps · H.264 · MP4", rows.Single(r => r.FormatSelector == "299+140").Label, StringComparison.Ordinal);
    }

    [Fact]
    public void Audio_only_rows_for_aac_and_opus()
    {
        var audio = YtDlpFormats.Build(Info, true, "en").Rows.Where(r => r.IsAudioOnly).ToList();
        Assert.Equal(new[] { "140", "251" }, audio.Select(r => r.FormatSelector)); // not "251-drc" (stable-volume variant), though its bitrate is higher
        Assert.Equal(("m4a", null), (audio[0].OutputExtension, audio[0].MergeFormat));
        Assert.StartsWith("Audio only · AAC · M4A · 129 kbps · ", audio[0].Label, StringComparison.Ordinal);
    }

    [Fact]
    public void The_original_sound_wins_over_an_auto_dub_in_the_users_language()
    {
        var choices = YtDlpFormats.Build(Info, true, "de-DE");
        Assert.Equal("137+140", choices.Rows.Single(r => r.Height == 1080 && r.Fps is not > 31).FormatSelector);
        Assert.Equal(new[] { "Sound: English (original)", "Sound: German" }, choices.AudioTracks.Select(a => a.Label));

        var german = YtDlpFormats.WithAudio(choices.Rows.First(r => r.FormatSelector == "137+140"), Info, choices.AudioTracks[1]);
        Assert.Equal(("137+140-de", "mp4"), (german.FormatSelector, german.MergeFormat)); // AAC fits H.264, though Opus has the higher bitrate
        var vp9German = YtDlpFormats.WithAudio(choices.Rows.First(r => r.Height == 1440), Info, choices.AudioTracks[1]);
        Assert.Equal(("271+251-de", "webm"), (vp9German.FormatSelector, vp9German.MergeFormat));
    }

    [Fact]
    public void Without_ffmpeg_only_formats_with_both_and_a_note()
    {
        var choices = YtDlpFormats.Build(Info, ffmpegAvailable: false, "en");
        Assert.Equal(new[] { "18", "140", "251" }, choices.Rows.Select(r => r.FormatSelector));
        var row = choices.Rows[0];
        Assert.Equal((null, "mp4", true), (row.MergeFormat, row.OutputExtension, row.IsEstimate));
        Assert.StartsWith("360p · H.264 · MP4 · ≈ ", row.Label, StringComparison.Ordinal);
        Assert.Equal(YtDlpFormats.NeedFfmpegNote, choices.Note);
        Assert.Empty(choices.AudioTracks); // can't merge another track without FFmpeg
    }

    [Fact]
    public void Without_ffmpeg_and_without_combined_formats_the_note_says_ffmpeg_is_needed()
    {
        var info = Info with { Formats = Info.Formats.Where(f => f.FormatId != "18").ToList() };
        var choices = YtDlpFormats.Build(info, false, "en");
        Assert.All(choices.Rows, r => Assert.True(r.IsAudioOnly));
        Assert.Equal(YtDlpFormats.NeedFfmpegOnlyNote, choices.Note);
    }

    [Fact]
    public void Refuses_drm_live_upcoming_and_playlists()
    {
        var drm = Info with { Formats = Info.Formats.Select(f => f with { HasDrm = true }).ToList() };
        Assert.Equal("Protected (DRM): can't be downloaded", YtDlpFormats.Build(drm, true, "en").Problem);
        Assert.Equal("Live stream: not supported yet", YtDlpFormats.Build(Info with { LiveStatus = YtDlpLiveStatus.Live }, true, "en").Problem);
        Assert.Equal("This video hasn't started yet.", YtDlpFormats.Build(Info with { LiveStatus = YtDlpLiveStatus.Upcoming }, true, "en").Problem);
        Assert.StartsWith("This page is a playlist", YtDlpFormats.Build(Info with { IsPlaylist = true }, true, "en").Problem, StringComparison.Ordinal);
        Assert.False(YtDlpFormats.Build(Info with { Formats = [] }, true, "en").CanDownload);
    }

    [Fact]
    public void Plain_codec_names_from_other_sites_are_understood()
    {
        var info = YtDlpInfoParser.Parse("""{"title":"Reel","duration":30,"formats":[{"format_id":"hd","ext":"mp4","vcodec":"h264","acodec":"aac","height":1280,"tbr":2000}]}""");
        var row = Assert.Single(YtDlpFormats.Build(info, false, "en").Rows);
        Assert.Equal(("hd", "mp4"), (row.FormatSelector, row.OutputExtension));
        Assert.StartsWith("1280p · H.264 · MP4 · ≈ ", row.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void A_strange_extension_from_the_json_never_reaches_the_file_name()
    {
        var info = YtDlpInfoParser.Parse("""{"title":"X","formats":[{"format_id":"a","ext":"..\\evil","vcodec":"avc1","acodec":"mp4a.40.2","height":480}]}""");
        Assert.Equal("mp4", Assert.Single(YtDlpFormats.Build(info, false, null).Rows).OutputExtension);
    }
}

public sealed class YtDlpArgumentsTests
{
    private static readonly YtDlpDownloadOptions Options = new(
        new Uri("https://www.youtube.com/watch?v=abc123XYZ00"), "137+140", "mp4", @"C:\Users\me\Downloads", "100% real – My video 1080p",
        @"C:\Users\me\AppData\Local\AJBrowser\Temp\ytdlp\7", @"C:\tools\ffmpeg.exe", @"C:\tools\deno.exe", @"C:\Users\me\AppData\Local\AJBrowser\Temp\ytdlp\7\cookies.txt", "Mozilla/5.0 Test");

    [Fact]
    public void Download_arguments_are_complete_and_safe()
    {
        var args = YtDlpArguments.Download(Options);
        Assert.Equal("--ignore-config", args[0]);
        Assert.Equal("utf-8", After(args, "--encoding")); // or non-Latin titles vanish from yt-dlp's output (F23)
        Assert.Contains("--no-playlist", args);
        Assert.Contains("--no-simulate", args); // --print would otherwise skip the download
        Assert.Equal("137+140", After(args, "-f"));
        Assert.Equal("mp4", After(args, "--merge-output-format"));
        Assert.Equal(@"C:\tools\ffmpeg.exe", After(args, "--ffmpeg-location"));
        Assert.Equal(@"deno:C:\tools\deno.exe", After(args, "--js-runtimes"));
        Assert.Equal(@"C:\Users\me\AppData\Local\AJBrowser\Temp\ytdlp\7\cookies.txt", After(args, "--cookies"));
        Assert.Equal("Mozilla/5.0 Test", After(args, "--user-agent"));
        Assert.Contains(@"home:C:\Users\me\Downloads", args);
        Assert.Contains(@"temp:C:\Users\me\AppData\Local\AJBrowser\Temp\ytdlp\7", args);
        Assert.Equal("100%% real – My video 1080p.%(ext)s", After(args, "-o"));
        Assert.Equal(("--", "https://www.youtube.com/watch?v=abc123XYZ00"), (args[^2], args[^1])); // the address can't be read as an option
        Assert.StartsWith("after_move:ajb-file:", After(args, "--print"), StringComparison.Ordinal);
    }

    [Fact]
    public void Optional_parts_are_left_out()
    {
        var args = YtDlpArguments.Download(Options with { MergeFormat = null, FfmpegPath = null, DenoPath = null, CookieFile = null, UserAgent = null, FormatSelector = "18" });
        Assert.DoesNotContain("--merge-output-format", args);
        Assert.DoesNotContain("--ffmpeg-location", args);
        Assert.DoesNotContain("--js-runtimes", args);
        Assert.DoesNotContain("--cookies", args);
        Assert.DoesNotContain("--user-agent", args);
    }

    [Theory]
    [InlineData("137+140; del *")]
    [InlineData("best[height<=720]")]
    [InlineData("")]
    public void Only_plain_format_ids_are_accepted(string selector) =>
        Assert.Throws<ArgumentException>(() => YtDlpArguments.Download(Options with { FormatSelector = selector }));

    [Fact]
    public void Only_web_pages_and_absolute_folders()
    {
        Assert.Throws<ArgumentException>(() => YtDlpArguments.Download(Options with { PageUrl = new Uri("file:///C:/x.html") }));
        Assert.Throws<ArgumentException>(() => YtDlpArguments.Download(Options with { OutputFolder = "Downloads" }));
        Assert.Throws<ArgumentException>(() => YtDlpArguments.Inspect(new Uri("ftp://x.example/v"), null, null, null));
    }

    [Fact]
    public void Inspect_arguments()
    {
        var args = YtDlpArguments.Inspect(new Uri("https://www.instagram.com/reel/XYZ/"), @"C:\d\deno.exe", null, "UA");
        Assert.Equal(new[] { "--ignore-config", "--no-playlist", "--encoding", "utf-8", "-J", "--js-runtimes", @"deno:C:\d\deno.exe", "--user-agent", "UA", "--", "https://www.instagram.com/reel/XYZ/" }, args);
    }

    private static string After(IReadOnlyList<string> args, string option) => args[args.ToList().IndexOf(option) + 1];
}

public sealed class YtDlpProgressTests
{
    [Fact]
    public void Parses_progress_lines_with_unknown_fields()
    {
        var line = YtDlpProgressTracker.ParseLine("ajb-progress 1048576 NA 5242880 250000.5 16 137")!;
        Assert.Equal((1_048_576L, 5_242_880L, true, 250_000.5, 16.0, "137"), (line.Downloaded, line.Total!.Value, line.TotalIsEstimate, line.BytesPerSecond!.Value, line.EtaSeconds!.Value, line.FormatId));
        Assert.Null(YtDlpProgressTracker.ParseLine("ajb-progress NA NA NA NA NA NA"));
        Assert.Null(YtDlpProgressTracker.ParseLine("[download]  42.0% of 10.00MiB"));
        Assert.Null(YtDlpProgressTracker.ParseLine("ajb-progress 1 2"));
    }

    [Fact]
    public void Combines_picture_and_sound_parts_and_follows_merge_and_final_path()
    {
        var tracker = new YtDlpProgressTracker("137+140", [300, 100]);
        Assert.Equal(400, tracker.Total);
        Assert.True(tracker.Read("ajb-progress 150 300 NA 1000 1 137"));
        Assert.Equal(150, tracker.Received);
        tracker.Read("ajb-progress 40 100 NA 900 1 140");
        Assert.Equal(340, tracker.Received); // the picture part counts as done once the sound runs
        Assert.Equal(YtDlpStage.Downloading, tracker.Stage);

        Assert.True(tracker.Read("[Merger] Merging formats into \"C:\\Users\\me\\Downloads\\My video.mp4\""));
        Assert.Equal(YtDlpStage.Merging, tracker.Stage);
        Assert.Null(tracker.BytesPerSecond);

        Assert.False(tracker.Read("[youtube] abc: Downloading webpage"));
        tracker.Read("ajb-file:relative\\path.mp4");
        Assert.Null(tracker.FinalPath); // only absolute paths are trusted
        tracker.Read(@"ajb-file:C:\Users\me\Downloads\My video.mp4");
        Assert.Equal((@"C:\Users\me\Downloads\My video.mp4", YtDlpStage.Done), (tracker.FinalPath, tracker.Stage));
    }

    [Fact]
    public void The_pickers_size_stands_in_until_every_part_is_known()
    {
        // Picture first (exact size known when it starts), sound later: the user still sees a total from the first byte.
        var tracker = new YtDlpProgressTracker("134+140", expectedTotal: 27_000_000);
        Assert.Equal(27_000_000, tracker.Total);
        tracker.Read("ajb-progress 1024 18294110 NA 97590 187 134");
        Assert.Equal(27_000_000, tracker.Total); // exact picture size known, sound not yet: the estimate covers it
        tracker.Read("ajb-progress 1024 10271496 NA 64661 158 140");
        Assert.Equal(18_294_110 + 10_271_496, tracker.Total); // now exact

        var low = new YtDlpProgressTracker("134+140", expectedTotal: 5_000_000); // an estimate that was too low
        low.Read("ajb-progress 9000000 18294110 NA 97590 100 134");
        Assert.Equal(18_294_110, low.Total); // never below what's known
    }

    [Fact]
    public void Learns_part_sizes_from_progress()
    {
        var tracker = new YtDlpProgressTracker("18");
        Assert.Null(tracker.Total);
        tracker.Read("ajb-progress 10 NA 80 NA NA 18");
        Assert.Equal((10L, 80L), (tracker.Received, tracker.Total!.Value));
    }
}

public sealed class YtDlpErrorsTests
{
    [Theory]
    [InlineData("ERROR: [youtube] abc: Sign in to confirm you’re not a bot. Use --cookies-from-browser or --cookies for the authentication.", YtDlpFix.SignIn)]
    [InlineData("ERROR: [youtube] abc: Sign in to confirm your age. This video may be inappropriate for some users.", YtDlpFix.SignIn)]
    [InlineData("ERROR: [instagram] XYZ: Requested content is not available, rate-limit reached or login required. Use --cookies", YtDlpFix.SignIn)]
    [InlineData("ERROR: [youtube] abc: Private video. Sign in if you've been granted access to this video", YtDlpFix.None)]
    [InlineData("ERROR: [youtube] abc: Video unavailable. This video has been removed by the uploader", YtDlpFix.None)]
    [InlineData("ERROR: [youtube] abc: The uploader has not made this video available in your country", YtDlpFix.None)]
    [InlineData("ERROR: [generic] Unsupported URL: https://example.com/page", YtDlpFix.None)]
    [InlineData("ERROR: [youtube] abc: This live event will begin in 3 hours.", YtDlpFix.None)]
    [InlineData("ERROR: [youtube] abc: Requested format is not available. Use --list-formats for a list of available formats", YtDlpFix.Retry)]
    [InlineData("ERROR: unable to download video data: HTTP Error 403: Forbidden", YtDlpFix.UpdateYtDlp)]
    [InlineData("ERROR: [youtube] abc: nsig extraction failed: Some formats may be missing", YtDlpFix.UpdateYtDlp)]
    [InlineData("WARNING: [youtube] No supported JavaScript runtime could be found. YouTube extraction without a JS runtime has been deprecated", YtDlpFix.InstallDeno)]
    [InlineData("ERROR: You have requested merging of multiple formats but ffmpeg is not installed. Aborting due to --abort-on-error", YtDlpFix.InstallFfmpeg)]
    [InlineData("ERROR: [youtube] abc: Unable to download webpage: <urlopen error [Errno 11001] getaddrinfo failed>", YtDlpFix.Retry)]
    public void Maps_known_errors(string line, YtDlpFix fix) =>
        Assert.Equal(fix, YtDlpErrors.Explain([line]).Fix);

    [Fact]
    public void A_missing_javascript_runtime_explains_missing_youtube_formats()
    {
        var problem = YtDlpErrors.Explain(
        [
            "WARNING: [youtube] No supported JavaScript runtime could be found. Only deno is enabled by default",
            "WARNING: [youtube] abc: Some web client https formats have been skipped as they are missing a url.",
            "ERROR: [youtube] abc: Requested format is not available. Use --list-formats for a list of available formats",
        ]);
        Assert.Equal(YtDlpFix.InstallDeno, problem.Fix);
        Assert.Equal(YtDlpFix.Retry, YtDlpErrors.Explain(["ERROR: [youtube] abc: Requested format is not available."]).Fix); // without the warning
    }

    [Fact]
    public void Private_videos_are_not_mistaken_for_sign_in()
    {
        Assert.Equal("This video is private.", YtDlpErrors.Explain(["ERROR: [youtube] abc: Private video. Sign in if you've been granted access to this video"]).Message);
    }

    [Fact]
    public void Unknown_errors_are_shown_redacted()
    {
        var problem = YtDlpErrors.Explain(["[info] something", "ERROR: Strange failure at https://rr1.googlevideo.example/videoplayback?sig=SECRET&expire=1 cookie: SID=abc"]);
        Assert.StartsWith("yt-dlp: Strange failure at https://rr1.googlevideo.example/videoplayback", problem.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", problem.Message);
        Assert.DoesNotContain("SID=abc", problem.Message);
        Assert.Equal(YtDlpFix.Retry, problem.Fix);
    }

    [Fact]
    public void Silence_still_gives_a_sentence() =>
        Assert.Equal(YtDlpFix.Retry, YtDlpErrors.Explain([]).Fix);

    [Fact]
    public void Detects_the_missing_js_runtime_warning() =>
        Assert.True(YtDlpErrors.MissesJsRuntime(["WARNING: [youtube] No supported JavaScript runtime could be found."]));
}

public sealed class NetscapeCookieFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AJBrowserTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Formats_cookies_the_way_yt_dlp_reads_them()
    {
        var text = NetscapeCookieFile.Format(
        [
            new BrowserCookie("SID", "abc123", ".youtube.com", "/", true, true, false, DateTimeOffset.FromUnixTimeSeconds(1_900_000_000)),
            new BrowserCookie("PREF", "f6=40000000", "www.youtube.com", "/", false, false, true, null),
            new BrowserCookie("BAD", "has\ttab", "youtube.com", "/", true, false, false, null),
            new BrowserCookie("", "nameless", "youtube.com", "/", true, false, false, null),
        ]);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(NetscapeCookieFile.Header, lines[0]);
        Assert.Contains("#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t1900000000\tSID\tabc123", lines);
        Assert.Contains("www.youtube.com\tFALSE\t/\tFALSE\t0\tPREF\tf6=40000000", lines);
        Assert.DoesNotContain(lines, l => l.Contains("BAD", StringComparison.Ordinal) || l.Contains("nameless", StringComparison.Ordinal));
    }

    [Fact]
    public void Writes_and_deletes_the_file()
    {
        var path = NetscapeCookieFile.Write(_root, [new BrowserCookie("a", "b", "x.example", "/", true, false, true, null)]);
        Assert.Equal(Path.Combine(_root, "cookies.txt"), path);
        var bytes = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, bytes[0]); // no BOM
        NetscapeCookieFile.Delete(_root);
        Assert.False(File.Exists(path));
        NetscapeCookieFile.Delete(Path.Combine(_root, "missing")); // never throws
    }

    [Fact]
    public void Cookie_scope_adds_google_sign_in_for_youtube_only()
    {
        Assert.Equal(new[] { "https://www.youtube.com/", "https://www.google.com/", "https://accounts.google.com/" },
            YtDlpCookieScope.UrlsFor(new Uri("https://www.youtube.com/watch?v=abc")).Select(u => u.AbsoluteUri));
        Assert.Equal(new[] { "https://www.instagram.com/" }, YtDlpCookieScope.UrlsFor(new Uri("https://www.instagram.com/reel/X/")).Select(u => u.AbsoluteUri));
        Assert.Equal(new[] { "https://evil-youtube.com/" }, YtDlpCookieScope.UrlsFor(new Uri("https://evil-youtube.com/watch")).Select(u => u.AbsoluteUri));
    }
}

public sealed class YtDlpSitesTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=aqz-KE-bpKQ", "YouTube")]
    [InlineData("https://m.youtube.com/watch?feature=share&v=aqz-KE-bpKQ", "YouTube")]
    [InlineData("https://www.youtube.com/shorts/abcDEF12345", "YouTube")]
    [InlineData("https://youtu.be/aqz-KE-bpKQ?t=10", "YouTube")]
    [InlineData("https://www.youtube-nocookie.com/embed/aqz-KE-bpKQ", "YouTube")]
    [InlineData("https://www.facebook.com/watch/?v=1234567890", "Facebook")]
    [InlineData("https://www.facebook.com/reel/1234567890", "Facebook")]
    [InlineData("https://www.facebook.com/SomePage/videos/1234567890/", "Facebook")]
    [InlineData("https://fb.watch/abcDEF/", "Facebook")]
    [InlineData("https://www.instagram.com/reel/C1a2B3c4D5e/", "Instagram")]
    [InlineData("https://www.instagram.com/p/C1a2B3c4D5e/", "Instagram")]
    [InlineData("https://www.tiktok.com/@some.user/video/7301234567890123456", "TikTok")]
    [InlineData("https://x.com/someone/status/1790000000000000000", "X")]
    [InlineData("https://twitter.com/someone/status/1790000000000000000", "X")]
    [InlineData("https://vimeo.com/76979871", "Vimeo")]
    [InlineData("https://www.reddit.com/r/videos/comments/abc123/some_title/", "Reddit")]
    [InlineData("https://www.twitch.tv/videos/1234567890", "Twitch")]
    [InlineData("https://www.dailymotion.com/video/x8abcde", "Dailymotion")]
    [InlineData("https://www.bilibili.com/video/BV1xx411c7mD", "Bilibili")]
    public void Recognises_video_pages(string url, string site) =>
        Assert.Equal(site, YtDlpSites.SiteOf(new Uri(url)));

    [Theory]
    [InlineData("https://www.youtube.com/")]
    [InlineData("https://www.youtube.com/@Blender")]
    [InlineData("https://www.youtube.com/results?search_query=bunny")]
    [InlineData("https://www.youtube.com/watch")]
    [InlineData("https://www.instagram.com/someuser/")]
    [InlineData("https://www.facebook.com/")]
    [InlineData("https://x.com/home")]
    [InlineData("https://vimeo.com/about")]
    [InlineData("https://evil-youtube.com/watch?v=aqz-KE-bpKQ")]
    [InlineData("https://youtube.com.evil.example/watch?v=aqz-KE-bpKQ")]
    [InlineData("https://example.com/watch?v=aqz-KE-bpKQ")]
    [InlineData("file:///C:/youtube.com/watch?v=aqz-KE-bpKQ")]
    public void Ignores_other_pages(string url) =>
        Assert.False(YtDlpSites.IsVideoPage(new Uri(url)));

    [Fact]
    public void Null_is_not_a_video_page() => Assert.False(YtDlpSites.IsVideoPage(null));
}
