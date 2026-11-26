using AJBrowser.Core.Media;
using AJBrowser.Core.Security;

namespace AJBrowser.Core.Tests.Media;

internal static class Obs
{
    public static MediaObservation Of(string url, string? type, int status = 200, long? length = null, string? range = null, string? context = "Media") =>
        new(new Uri(url), status, type, length, range, context);
}

public sealed class MediaClassifierTests
{
    [Theory]
    // Direct files
    [InlineData("https://cdn.example/v/clip.mp4", "video/mp4", MediaKind.Direct)]
    [InlineData("https://cdn.example/v/clip.webm", "video/webm; codecs=vp9", MediaKind.Direct)]
    [InlineData("https://cdn.example/v/song.mp3", "audio/mpeg", MediaKind.Direct)]
    [InlineData("https://cdn.example/v/clip.mp4", "application/octet-stream", MediaKind.Direct)]
    [InlineData("https://cdn.example/v/clip.mov", null, MediaKind.Direct)]
    [InlineData("https://cdn.example/watch?id=5", "video/mp4", MediaKind.Direct)]
    // HLS (spike F1: Mux served its playlist as audio/mpegurl; some Apple playlists had no type)
    [InlineData("https://stream.mux.com/abc.m3u8", "audio/mpegurl", MediaKind.Hls)]
    [InlineData("https://cdn.example/master.m3u8?token=x", null, MediaKind.Hls)]
    [InlineData("https://cdn.example/play", "application/vnd.apple.mpegurl", MediaKind.Hls)]
    [InlineData("https://cdn.example/play", "application/x-mpegURL", MediaKind.Hls)]
    // DASH
    [InlineData("https://cdn.example/manifest.mpd", "application/dash+xml", MediaKind.Dash)]
    [InlineData("https://cdn.example/manifest.mpd", "text/plain", MediaKind.Dash)]
    // Segments (spike F1: TS pieces untyped or octet-stream; DASH pieces as video/mp4)
    [InlineData("https://cdn.example/seg/00012.ts", "application/octet-stream", MediaKind.Segment)]
    [InlineData("https://cdn.example/seg/00012.ts", null, MediaKind.Segment)]
    [InlineData("https://cdn.example/seg/x", "video/mp2t", MediaKind.Segment)]
    [InlineData("https://cdn.example/dash/chunk-v1-0003.m4s", "video/mp4", MediaKind.Segment)]
    [InlineData("https://cdn.example/dash/init.mp4", "video/mp4", MediaKind.Segment)]
    [InlineData("https://cdn.example/dash/init-a1.m4a", "audio/mp4", MediaKind.Segment)]
    [InlineData("https://cdn.example/dash/segment_17.m4v", "video/mp4", MediaKind.Segment)]
    [InlineData("https://cdn.example/hls/frag5.mp4", "video/mp4", MediaKind.Segment)]
    [InlineData("https://cdn.example/audio/0001.aac", "audio/aac", MediaKind.Segment)]
    [InlineData("https://cdn.example/subs/en.vtt", "text/vtt", MediaKind.Segment)]
    // Not media
    [InlineData("https://example.com/", "text/html", MediaKind.None)]
    [InlineData("https://example.com/app.js", "application/javascript", MediaKind.None)]
    [InlineData("https://example.com/file.bin", "application/octet-stream", MediaKind.None)]
    [InlineData("https://example.com/poster.jpg", "image/jpeg", MediaKind.None)]
    public void Classifies(string url, string? type, MediaKind expected) =>
        Assert.Equal(expected, MediaClassifier.Classify(Obs.Of(url, type)).Kind);

    [Theory]
    [InlineData(404)]
    [InlineData(302)]
    [InlineData(304)]
    public void Ignores_responses_without_the_file(int status) =>
        Assert.Equal(MediaKind.None, MediaClassifier.Classify(Obs.Of("https://cdn.example/v.mp4", "video/mp4", status)).Kind);

    [Fact]
    public void Ignores_non_media_contexts_and_non_http()
    {
        Assert.Equal(MediaKind.None, MediaClassifier.Classify(Obs.Of("https://cdn.example/v.mp4", "video/mp4", context: "Image")).Kind);
        Assert.Equal(MediaKind.None, MediaClassifier.Classify(Obs.Of("file:///C:/v.mp4", "video/mp4")).Kind);
    }

    [Fact]
    public void Reports_container_and_audio_only()
    {
        var audio = MediaClassifier.Classify(Obs.Of("https://cdn.example/a.m4a", "audio/mp4"));
        Assert.Equal(("M4A", true), (audio.Container, audio.IsAudioOnly));
        Assert.Equal("WebM", MediaClassifier.Classify(Obs.Of("https://cdn.example/a.webm", "video/webm")).Container);
    }

    [Theory]
    [InlineData(206, null, "bytes 0-1023/86000000", 86_000_000L)]
    [InlineData(206, 1024L, "bytes 1024-2047/*", null)]
    [InlineData(206, 1024L, null, null)]
    [InlineData(200, 5_000_000L, null, 5_000_000L)]
    [InlineData(200, null, null, null)]
    public void Works_out_the_whole_file_size(int status, long? length, string? range, long? expected) =>
        Assert.Equal(expected, MediaClassifier.TotalSize(Obs.Of("https://cdn.example/v.mp4", "video/mp4", status, length, range)));

    [Fact]
    public void Key_ignores_query_and_host_case() =>
        Assert.Equal(
            MediaClassifier.KeyOf(new Uri("https://CDN.example/v/a.mp4?token=1")),
            MediaClassifier.KeyOf(new Uri("https://cdn.example/v/a.mp4?token=2&x=y")));
}

public sealed class TabMediaStoreTests
{
    private const string Mp4 = "https://cdn.example/v/movie.mp4";

    [Fact]
    public void Many_range_requests_of_one_file_are_one_video()
    {
        var store = new TabMediaStore();
        Assert.True(store.Add(Obs.Of(Mp4 + "?t=1", "video/mp4", 206, 65536, "bytes 0-65535/50000000")));
        Assert.False(store.Add(Obs.Of(Mp4 + "?t=2", "video/mp4", 206, 65536, "bytes 65536-131071/50000000")));
        Assert.False(store.Add(Obs.Of(Mp4 + "?t=3", "video/mp4", 206, 65536, "bytes 131072-196607/50000000")));

        var item = Assert.Single(store.Visible);
        Assert.Equal(3, item.RequestCount);
        Assert.Equal(50_000_000, item.TotalBytes);
        Assert.Equal("t=3", item.Url.Query.TrimStart('?')); // newest address (tokens expire)
        Assert.True(item.IsConfirmed);
    }

    [Fact]
    public void Small_files_are_not_confirmed_videos()
    {
        var store = new TabMediaStore();
        store.Add(Obs.Of("https://cdn.example/hover-preview.mp4", "video/mp4", 200, 300_000));
        Assert.Empty(store.Visible);
        Assert.Single(store.Items); // remembered, just not shown
    }

    [Fact]
    public void Files_fetched_by_page_script_are_stream_pieces_not_videos()
    {
        // Spike F1: Apple's fMP4 HLS fetches byte ranges of one 82 MB main.mp4 with fetch/XHR.
        var store = new TabMediaStore();
        store.Add(Obs.Of("https://devstreaming.example/hls/main.mp4", "video/mp4", 206, 1_000_000, "bytes 0-999999/82000000", "Fetch"));
        store.Add(Obs.Of("https://cdn.example/other.mp4", "video/mp4", 200, 20_000_000, context: "XmlHttpRequest"));
        Assert.Empty(store.Visible);
    }

    [Fact]
    public void The_pages_player_confirms_a_file_and_gives_its_size()
    {
        var store = new TabMediaStore();
        store.Add(Obs.Of(Mp4, "video/mp4", 206, 1000, "bytes 0-999/*", "Fetch")); // unknown size, script-loaded
        Assert.Empty(store.Visible);

        Assert.True(store.ApplyHint(new VideoHint(new Uri(Mp4 + "?other=token"), false, 1280, 720, 63.5)));
        var item = Assert.Single(store.Visible);
        Assert.Equal("1280×720", item.Resolution);
        Assert.Equal(63.5, item.DurationSeconds);

        store.Add(Obs.Of(Mp4, "video/mp4", 206, 1000, "bytes 1000-1999/*", "Fetch"));
        Assert.True(Assert.Single(store.Visible).IsConfirmed); // stays confirmed
    }

    [Fact]
    public void Hints_never_add_videos_on_their_own()
    {
        var store = new TabMediaStore();
        Assert.False(store.ApplyHint(new VideoHint(new Uri("https://evil.example/fake.mp4"), false, 1920, 1080, 10)));
        Assert.Empty(store.Items);
    }

    [Fact]
    public void Segments_are_never_stored_and_a_stream_flood_is_one_entry()
    {
        var store = new TabMediaStore();
        for (var i = 0; i < 50; i++)
        {
            store.Add(Obs.Of($"https://cdn.example/live/seg{i}.ts", "video/mp2t", context: "Fetch"));
            store.Add(Obs.Of($"https://cdn.example/live/index.m3u8?_HLS_msn={i}", "application/vnd.apple.mpegurl", context: "Fetch")); // live refresh
        }

        var item = Assert.Single(store.Items);
        Assert.Equal(MediaKind.Hls, item.Kind);
        Assert.Equal(50, item.RequestCount);
        Assert.Equal(1, store.VisibleCount); // streams are listed ("supported soon")
    }

    [Fact]
    public void A_new_page_clears_the_list_and_spa_navigation_flags_new_items()
    {
        var store = new TabMediaStore();
        store.Add(Obs.Of(Mp4, "video/mp4", 200, 5_000_000));
        store.MarkSpaNavigation();
        store.Add(Obs.Of("https://cdn.example/v/next.mp4", "video/mp4", 200, 5_000_000));

        Assert.Equal([false, true], store.Visible.Select(v => v.IsNewSinceSpaNavigation));

        var version = store.Version;
        store.Reset();
        Assert.Empty(store.Items);
        Assert.NotEqual(version, store.Version);
    }

    [Fact]
    public void Playlists_a_master_refers_to_are_hidden_now_and_later()
    {
        var store = new TabMediaStore();
        store.Add(Obs.Of("https://cdn.example/s/master.m3u8", "application/vnd.apple.mpegurl", context: "Fetch"));
        store.Add(Obs.Of("https://cdn.example/s/v720.m3u8?t=1", "application/vnd.apple.mpegurl", context: "Fetch"));
        Assert.Equal(2, store.VisibleCount);

        Assert.True(store.MarkReferenced([new Uri("https://cdn.example/s/v720.m3u8"), new Uri("https://cdn.example/s/en.m3u8")]));
        Assert.Equal("https://cdn.example/s/master.m3u8", Assert.Single(store.Visible).Url.AbsoluteUri);

        store.Add(Obs.Of("https://cdn.example/s/en.m3u8", "application/vnd.apple.mpegurl", context: "Fetch")); // arrives later
        Assert.Single(store.Visible);

        store.Reset();
        store.Add(Obs.Of("https://cdn.example/s/v720.m3u8", "application/vnd.apple.mpegurl", context: "Fetch"));
        Assert.Single(store.Visible); // a new page starts fresh
    }

    [Fact]
    public void A_page_cant_flood_the_store()
    {
        var store = new TabMediaStore();
        for (var i = 0; i < TabMediaStore.MaxItems + 50; i++)
        {
            store.Add(Obs.Of($"https://cdn.example/v{i}.mp4", "video/mp4", 200, 5_000_000));
        }

        Assert.Equal(TabMediaStore.MaxItems, store.Items.Count);
    }

    [Fact]
    public void Keeps_the_file_name_the_server_sends()
    {
        var store = new TabMediaStore();
        store.Add(Obs.Of(Mp4, "video/mp4", 206, 1000, "bytes 0-999/5000000"));
        store.Add(Obs.Of(Mp4, "video/mp4", 206, 1000, "bytes 1000-1999/5000000") with { ContentDisposition = "inline; filename*=UTF-8''Caf%C3%A9%20tour.mp4" });
        Assert.Equal("Café tour.mp4", Assert.Single(store.Visible).ServerFileName);
    }

    [Fact]
    public void Display_url_hides_tokens()
    {
        var store = new TabMediaStore();
        store.Add(Obs.Of("https://cdn.example/v/d17_CixjLkPq2bX9mNa3/movie.mp4?sig=SECRET", "video/mp4", 200, 5_000_000));
        var shown = Assert.Single(store.Visible).DisplayUrl;
        Assert.DoesNotContain("SECRET", shown);
        Assert.DoesNotContain("Cixj", shown);
    }
}

public sealed class VideoHintTests
{
    [Fact]
    public void Parses_a_valid_message()
    {
        var hint = VideoHint.TryParse("""{"type":"ajb-video","currentSrc":"https://cdn.example/a.mp4","videoWidth":1920,"videoHeight":1080,"duration":12.5}""");
        Assert.NotNull(hint);
        Assert.Equal("https://cdn.example/a.mp4", hint.CurrentSrc!.AbsoluteUri);
        Assert.Equal((1920, 1080, 12.5), (hint.Width, hint.Height, hint.DurationSeconds));
        Assert.False(hint.IsBlob);
    }

    [Fact]
    public void Blob_sources_are_flagged_without_an_address()
    {
        var hint = VideoHint.TryParse("""{"type":"ajb-video","currentSrc":"blob:https://site.example/1234","videoWidth":1280,"videoHeight":720}""");
        Assert.True(hint!.IsBlob);
        Assert.Null(hint.CurrentSrc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"type":"something-else","currentSrc":"https://a.example/x.mp4"}""")]
    [InlineData("""{"currentSrc":"https://a.example/x.mp4"}""")]
    public void Rejects_anything_else(string? json) => Assert.Null(VideoHint.TryParse(json));

    [Fact]
    public void Rejects_oversized_messages() =>
        Assert.Null(VideoHint.TryParse("""{"type":"ajb-video","currentSrc":"https://a.example/""" + new string('x', 5000) + "\"}"));

    [Fact]
    public void Drops_bad_fields_but_keeps_the_rest()
    {
        var hint = VideoHint.TryParse("""{"type":"ajb-video","currentSrc":"javascript:alert(1)","videoWidth":-5,"videoHeight":999999,"duration":"long"}""");
        Assert.NotNull(hint);
        Assert.Null(hint.CurrentSrc);
        Assert.Null(hint.Width);
        Assert.Null(hint.Height);
        Assert.Null(hint.DurationSeconds);
    }
}

public sealed class RequestContextTests
{
    [Fact]
    public void Copies_only_the_allowlisted_headers()
    {
        var context = RequestContext.FromObservedRequest(
        [
            new("referer", "https://site.example/watch"),
            new("User-Agent", "Mozilla/5.0"),
            new("Accept-Language", "en-US"),
            new("Origin", "https://site.example"),
            new("Cookie", "session=SECRET"),
            new("Authorization", "Bearer SECRET"),
            new("X-Api-Key", "SECRET"),
            new("Range", "bytes=0-"),
        ], pageUrl: null);

        Assert.Equal(["Accept-Language", "Origin", "Referer", "User-Agent"], context.Headers.Keys.Order());
        Assert.DoesNotContain(context.Headers.Values, v => v.Contains("SECRET"));
    }

    [Fact]
    public void Refuses_header_injection_and_huge_values()
    {
        var context = RequestContext.FromObservedRequest(
        [
            new("Referer", "https://site.example/\r\nCookie: stolen=1"),
            new("User-Agent", new string('a', RequestContext.MaxValueLength + 1)),
        ], pageUrl: null);
        Assert.Empty(context.Headers);
    }

    [Fact]
    public void Falls_back_to_the_page_origin_as_referer()
    {
        var context = RequestContext.FromObservedRequest([], new Uri("https://site.example/private/page?id=7"));
        Assert.Equal("https://site.example/", context.Headers["Referer"]);
    }

    [Fact]
    public void Cookies_are_added_only_at_download_time()
    {
        var context = RequestContext.FromObservedRequest([new("Referer", "https://site.example/")], null);
        Assert.False(context.ToHeaders(null).ContainsKey("Cookie"));
        Assert.Equal("a=1; b=2", context.ToHeaders("a=1; b=2")["Cookie"]);
        Assert.False(context.ToHeaders("a=1\r\nX: y").ContainsKey("Cookie"));
        Assert.False(context.Headers.ContainsKey("Cookie")); // the stored context never holds cookies
    }
}

public sealed class MediaUrlRedactionTests
{
    [Theory]
    [InlineData("https://cdn.example/live/use1/d17_CixjLkPq2bX9mNa3/master.m3u8", "https://cdn.example/live/use1/[id]/master.m3u8")]
    [InlineData("https://cdn.example/v/3f2a9c4e-1b7d-4e8a-9c3f-0a1b2c3d4e5f.mp4?sig=x", "https://cdn.example/v/[id].mp4?[redacted]")]
    [InlineData("https://cdn.example/videos/2024/my-holiday-trip.mp4", "https://cdn.example/videos/2024/my-holiday-trip.mp4")]
    [InlineData("https://cdn.example/v/video_1080p_v2.mp4", "https://cdn.example/v/video_1080p_v2.mp4")]
    [InlineData("https://user:pass@cdn.example/a.mp4#t=5", "https://cdn.example/a.mp4#[redacted]")]
    public void Shortens_tokens_in_paths(string url, string expected) => Assert.Equal(expected, Redactor.MediaUrl(url));

    [Fact]
    public void Non_http_addresses_stay_fully_hidden() => Assert.Equal("blob:[redacted]", Redactor.MediaUrl("blob:https://site.example/123"));
}

public sealed class MediaFileNamerTests
{
    private static DetectedMedia Media(string url, string? container = "MP4", int? height = null, bool audio = false) => new()
    {
        Id = 1,
        Kind = MediaKind.Direct,
        Key = url,
        Url = new Uri(url),
        Container = container,
        Height = height,
        IsAudioOnly = audio,
    };

    [Theory]
    [InlineData("https://cdn.example/v/a.mp4", "My trip: day 1", 720, false, "MP4", "My trip_ day 1 720p.mp4")]
    [InlineData("https://cdn.example/v/clip.webm", "", null, false, "WebM", "clip.webm")]
    [InlineData("https://cdn.example/v/clip.webm", "https://cdn.example/v/clip.webm", null, false, "WebM", "clip.webm")]
    [InlineData("https://cdn.example/a/song.m4a", "Album", null, true, "M4A", "Album.m4a")]
    [InlineData("https://cdn.example/watch", "Lecture 1080p", 1080, false, "MP4", "Lecture 1080p.mp4")]
    [InlineData("https://cdn.example/", null, null, false, null, "video.mp4")]
    public void Suggests_names(string url, string? title, int? height, bool audio, string? container, string expected) =>
        Assert.Equal(expected, MediaFileNamer.Suggest(Media(url, container, height, audio), title));

    [Fact]
    public void The_servers_file_name_wins()
    {
        var media = Media("https://cdn.example/f/8c1d2e3f4a5b6c7d8e9f.mkv", "MKV", 1080) with
        {
            ServerFileName = "Pritam and Pedro S01E03 The Blackmail.1080p.WEB-DL.Multi.DD+5.1.x264-KIN.mkv",
        };
        Assert.Equal("Pritam and Pedro S01E03 The Blackmail.1080p.WEB-DL.Multi.DD+5.1.x264-KIN.mkv",
            MediaFileNamer.Suggest(media, "MyPeerCloud - Personal Cloud"));
    }

    [Fact]
    public void A_real_name_in_the_address_beats_the_page_title() =>
        Assert.Equal("Pritam and Pedro S01E03.mkv",
            MediaFileNamer.Suggest(Media("https://cdn.example/files/Pritam%20and%20Pedro%20S01E03.mkv", "MKV", 1080), "MyPeerCloud - Personal Cloud"));

    [Theory]
    [InlineData("https://cdn.example/v/d17_CixjLkPq2bX9mNa3.mp4")]
    [InlineData("https://cdn.example/v/3f2a9c4e-1b7d-4e8a-9c3f-0a1b2c3d4e5f.mp4")]
    [InlineData("https://cdn.example/v/videoplayback.mp4")]
    [InlineData("https://cdn.example/v/master.mp4")]
    public void Tokens_and_generic_names_in_the_address_fall_back_to_the_title(string url) =>
        Assert.Equal("Holiday 720p.mp4", MediaFileNamer.Suggest(Media(url, "MP4", 720), "Holiday"));

    [Fact]
    public void A_server_name_without_extension_gets_one() =>
        Assert.Equal("Lecture 4.mp4", MediaFileNamer.Suggest(Media("https://cdn.example/x") with { ServerFileName = "Lecture 4" }, null));

    [Theory]
    [InlineData("Pritam and Pedro S01E03", true)]
    [InlineData("my-holiday-trip", true)]
    [InlineData("d17_CixjLkPq2bX9mNa3", false)]
    [InlineData("video", false)]
    [InlineData("song", false)]
    [InlineData("12345", false)]
    public void Judges_names(string stem, bool expected) => Assert.Equal(expected, MediaFileNamer.IsMeaningful(stem, lenient: false));

    [Fact]
    public void Long_titles_are_cut() =>
        Assert.True(MediaFileNamer.Suggest(Media("https://cdn.example/a.mp4"), new string('x', 300)).Length <= MediaFileNamer.MaxTitleLength + 4);
}
