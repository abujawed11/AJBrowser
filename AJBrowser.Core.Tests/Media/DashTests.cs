using AJBrowser.Core.Media.Dash;

namespace AJBrowser.Core.Tests.Media;

public sealed class DashParserTests
{
    private static readonly Uri ManifestUri = new("https://cdn.example/videos/show/manifest.mpd?token=abc");

    // SegmentTemplate with $Number$ and a fixed duration, inherited from the AdaptationSet, BaseURL at three levels.
    private const string NumberTemplate = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT10.5S" minBufferTime="PT2S">
          <BaseURL>media/</BaseURL>
          <Period id="p0">
            <AdaptationSet contentType="video" mimeType="video/mp4" segmentAlignment="true">
              <BaseURL>video/</BaseURL>
              <SegmentTemplate timescale="1000" duration="4000" startNumber="1"
                               initialization="$RepresentationID$/init.mp4" media="$RepresentationID$/seg-$Number%03d$.m4s" />
              <Representation id="v1080" bandwidth="4800000" width="1920" height="1080" codecs="avc1.640028" frameRate="30000/1001" />
              <Representation id="v360" bandwidth="800000" width="640" height="360" codecs="avc1.4d401e">
                <SegmentTemplate startNumber="10" />
              </Representation>
            </AdaptationSet>
            <AdaptationSet mimeType="audio/mp4" lang="en-US" codecs="mp4a.40.2">
              <Role schemeIdUri="urn:mpeg:dash:role:2011" value="main" />
              <AudioChannelConfiguration schemeIdUri="urn:mpeg:dash:23003:3:audio_channel_configuration:2011" value="2" />
              <SegmentTemplate timescale="48000" duration="192000" initialization="https://audio-cdn.example/$RepresentationID$/init.mp4" media="https://audio-cdn.example/$RepresentationID$/$Number$.m4s" />
              <Representation id="a128" bandwidth="128000" />
            </AdaptationSet>
            <AdaptationSet mimeType="text/vtt" lang="en">
              <Representation id="sub" bandwidth="1000"><BaseURL>subs/en.vtt</BaseURL></Representation>
            </AdaptationSet>
          </Period>
        </MPD>
        """;

    [Fact]
    public void Number_template_lists_every_piece_with_inherited_settings_and_resolved_addresses()
    {
        var manifest = DashParser.Parse(NumberTemplate, ManifestUri);
        Assert.False(manifest.IsDynamic);
        Assert.Equal(10.5, manifest.DurationSeconds);
        Assert.Equal(1, manifest.PeriodCount);
        Assert.Equal(4, manifest.Representations.Count);

        var best = manifest.Representations[0];
        Assert.Equal((DashTrackKind.Video, "v1080", 4_800_000L, 1920, 1080), (best.Kind, best.Id, best.Bandwidth, best.Width!.Value, best.Height!.Value));
        Assert.Equal(29.97, best.FrameRate!.Value, 2);
        Assert.Equal("https://cdn.example/videos/show/media/video/v1080/init.mp4", best.Initialization!.Uri.AbsoluteUri);
        Assert.Equal(3, best.Segments.Count); // ceil(10.5 / 4)
        Assert.Equal("https://cdn.example/videos/show/media/video/v1080/seg-001.m4s", best.Segments[0].Uri.AbsoluteUri);
        Assert.Equal("https://cdn.example/videos/show/media/video/v1080/seg-003.m4s", best.Segments[2].Uri.AbsoluteUri);
        Assert.Equal(new[] { 4.0, 4.0, 2.5 }, best.Segments.Select(s => s.DurationSeconds)); // the last piece is shorter
        Assert.Equal(10.5, best.SegmentDurationSeconds, 3);
    }

    [Fact]
    public void A_representation_overrides_only_the_template_attributes_it_sets()
    {
        var low = DashParser.Parse(NumberTemplate, ManifestUri).Representations[1];
        Assert.Equal("https://cdn.example/videos/show/media/video/v360/seg-010.m4s", low.Segments[0].Uri.AbsoluteUri);
        Assert.Equal("https://cdn.example/videos/show/media/video/v360/init.mp4", low.Initialization!.Uri.AbsoluteUri);
    }

    [Fact]
    public void Reads_audio_tracks_with_language_role_channels_and_absolute_addresses()
    {
        var manifest = DashParser.Parse(NumberTemplate, ManifestUri);
        var audio = Assert.Single(manifest.Audio);
        Assert.Equal((DashTrackKind.Audio, "en-US", 2, true), (audio.Kind, audio.Language, audio.AudioChannels!.Value, audio.IsMainRole));
        Assert.Equal("https://audio-cdn.example/a128/init.mp4", audio.Initialization!.Uri.AbsoluteUri);
        Assert.Equal(3, audio.Segments.Count); // 504000 / 192000 at timescale 48000
        Assert.Equal("https://audio-cdn.example/a128/1.m4s", audio.Segments[0].Uri.AbsoluteUri);
    }

    [Fact]
    public void Subtitles_are_text_tracks_and_not_video_or_audio()
    {
        var manifest = DashParser.Parse(NumberTemplate, ManifestUri);
        var text = manifest.Representations.Single(r => r.Id == "sub");
        Assert.Equal(DashTrackKind.Text, text.Kind);
        Assert.DoesNotContain(text, manifest.Video);
        Assert.DoesNotContain(text, manifest.Audio);
    }

    [Fact]
    public void Timeline_with_time_addresses_repeats_and_open_ended_repeat()
    {
        const string mpd = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT0H0M20S">
              <Period>
                <AdaptationSet mimeType="video/mp4" codecs="avc1.64001f" width="1280" height="720">
                  <SegmentTemplate timescale="90000" initialization="init-$Bandwidth$.mp4" media="chunk-$Bandwidth$-$Time$.m4s">
                    <SegmentTimeline>
                      <S t="900" d="180000" r="2" />
                      <S d="90000" />
                      <S d="270000" r="-1" />
                    </SegmentTimeline>
                  </SegmentTemplate>
                  <Representation id="720" bandwidth="2500000" />
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var video = DashParser.Parse(mpd, ManifestUri).Representations.Single();
        Assert.Equal("https://cdn.example/videos/show/init-2500000.mp4", video.Initialization!.Uri.AbsoluteUri);
        var times = video.Segments.Select(s => s.Uri.AbsolutePath.Split('-').Last().Replace(".m4s", "")).ToList();
        // 3 × 2 s from t=900, then 1 s, then 3 s pieces until 20 s (1,800,000 at 90 kHz): 630900 → 1170900 (2 more).
        Assert.Equal(new[] { "900", "180900", "360900", "540900", "630900", "900900", "1170900", "1440900", "1710900" }, times);
        Assert.Equal(2.0, video.Segments[0].DurationSeconds);
        Assert.Equal(1.0, video.Segments[3].DurationSeconds);
        Assert.Equal(3.0, video.Segments[^1].DurationSeconds);
    }

    [Fact]
    public void Timeline_with_number_addresses_counts_from_start_number()
    {
        const string mpd = """
            <MPD type="static" mediaPresentationDuration="PT6S">
              <Period>
                <AdaptationSet mimeType="audio/mp4" codecs="mp4a.40.2">
                  <Representation id="a" bandwidth="96000">
                    <SegmentTemplate timescale="1" startNumber="5" media="a/$Number$.m4s">
                      <SegmentTimeline><S d="2" r="2" /></SegmentTimeline>
                    </SegmentTemplate>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var audio = DashParser.Parse(mpd, ManifestUri).Audio.Single();
        Assert.Equal(new[] { "/videos/show/a/5.m4s", "/videos/show/a/6.m4s", "/videos/show/a/7.m4s" }, audio.Segments.Select(s => s.Uri.AbsolutePath));
        Assert.Null(audio.Initialization);
    }

    [Fact]
    public void Segment_list_with_byte_ranges_and_an_initialization_range()
    {
        const string mpd = """
            <MPD type="static" mediaPresentationDuration="PT9S">
              <Period>
                <AdaptationSet mimeType="video/mp4" codecs="avc1.4d401f">
                  <Representation id="v" bandwidth="1000000" height="480">
                    <BaseURL>https://files.example/movie-480.mp4</BaseURL>
                    <SegmentList timescale="1000" duration="3000">
                      <Initialization range="0-861" />
                      <SegmentURL mediaRange="862-50000" />
                      <SegmentURL mediaRange="50001-99000" />
                      <SegmentURL media="tail.m4s" />
                    </SegmentList>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var video = DashParser.Parse(mpd, ManifestUri).Video.Single();
        Assert.Equal("https://files.example/movie-480.mp4", video.Initialization!.Uri.AbsoluteUri);
        Assert.Equal("bytes=0-861", video.Initialization.ByteRange!.ToRangeHeader());
        Assert.Equal(3, video.Segments.Count);
        Assert.Equal("bytes=862-50000", video.Segments[0].ByteRange!.ToRangeHeader());
        Assert.Equal("https://files.example/tail.m4s", video.Segments[2].Uri.AbsoluteUri);
        Assert.Null(video.Segments[2].ByteRange);
        Assert.Equal(3.0, video.Segments[0].DurationSeconds);
    }

    [Fact]
    public void Segment_base_and_bare_base_url_are_one_whole_file()
    {
        const string mpd = """
            <MPD type="static" mediaPresentationDuration="PT1M">
              <Period>
                <AdaptationSet mimeType="video/webm" codecs="vp9">
                  <Representation id="v" bandwidth="2000000" height="720">
                    <BaseURL>v720.webm</BaseURL>
                    <SegmentBase indexRange="300-900"><Initialization range="0-299" /></SegmentBase>
                  </Representation>
                </AdaptationSet>
                <AdaptationSet mimeType="audio/webm" codecs="opus">
                  <Representation id="a" bandwidth="160000"><BaseURL>audio.webm</BaseURL></Representation>
                  <Representation id="no-address" bandwidth="64000" />
                </AdaptationSet>
              </Period>
            </MPD>
            """;

        var manifest = DashParser.Parse(mpd, ManifestUri);
        var video = manifest.Video.Single();
        Assert.True(video.IsSingleFile);
        Assert.Equal("https://cdn.example/videos/show/v720.webm", video.Segments[0].Uri.AbsoluteUri);
        Assert.Equal(60, video.Segments[0].DurationSeconds);
        Assert.True(manifest.Audio.Single().IsSingleFile);
        Assert.DoesNotContain(manifest.Representations, r => r.Id == "no-address"); // its "file" would be the manifest
    }

    [Fact]
    public void Video_with_an_audio_codec_is_muxed()
    {
        const string mpd = """
            <MPD type="static" mediaPresentationDuration="PT5S"><Period>
              <AdaptationSet mimeType="video/mp4"><Representation id="m" bandwidth="1" codecs="avc1.42c01e,mp4a.40.2"><BaseURL>m.mp4</BaseURL></Representation></AdaptationSet>
            </Period></MPD>
            """;
        Assert.Equal(DashTrackKind.Muxed, DashParser.Parse(mpd, ManifestUri).Video.Single().Kind);
    }

    [Theory]
    [InlineData("$Number$/x/$Unknown$.m4s")] // unknown identifier
    [InlineData("seg-$Number.m4s")] // unclosed
    public void A_track_with_a_bad_template_is_skipped(string media)
    {
        var mpd = $"""
            <MPD type="static" mediaPresentationDuration="PT8S"><Period>
              <AdaptationSet mimeType="video/mp4">
                <Representation id="bad" bandwidth="1"><SegmentTemplate duration="4" media="{media}" /></Representation>
                <Representation id="good" bandwidth="1"><SegmentTemplate duration="4" media="g$Number$.m4s" /></Representation>
              </AdaptationSet>
            </Period></MPD>
            """;
        Assert.Equal("good", Assert.Single(DashParser.Parse(mpd, ManifestUri).Representations).Id);
    }

    [Fact]
    public void Number_template_without_a_known_length_is_skipped()
    {
        const string mpd = """
            <MPD type="static"><Period><AdaptationSet mimeType="video/mp4">
              <Representation id="v" bandwidth="1"><SegmentTemplate duration="4" media="$Number$.m4s" /></Representation>
            </AdaptationSet></Period></MPD>
            """;
        Assert.Empty(DashParser.Parse(mpd, ManifestUri).Representations);
    }

    [Fact]
    public void Only_http_addresses_are_kept()
    {
        const string mpd = """
            <MPD type="static" mediaPresentationDuration="PT4S"><Period><AdaptationSet mimeType="video/mp4">
              <Representation id="v" bandwidth="1"><BaseURL>file:///C:/Windows/win.ini</BaseURL></Representation>
              <Representation id="w" bandwidth="1"><SegmentTemplate duration="4" media="javascript:alert($Number$)" /></Representation>
            </AdaptationSet></Period></MPD>
            """;
        Assert.Empty(DashParser.Parse(mpd, ManifestUri).Representations);
    }

    [Theory]
    [InlineData("<html><body>not a manifest</body></html>")]
    [InlineData("<MPD><Period>")] // broken XML
    [InlineData("<MPD type=\"static\"></MPD>")] // no period
    [InlineData("<?xml version=\"1.0\"?><!DOCTYPE MPD [<!ENTITY x \"boom\">]><MPD><Period/></MPD>")] // DTDs refused
    [InlineData("#EXTM3U\n#EXT-X-ENDLIST")]
    public void Rejects_things_that_are_not_manifests(string text)
    {
        Assert.Throws<FormatException>(() => DashParser.Parse(text, ManifestUri));
        Assert.Null(DashParser.TryParse(text, ManifestUri));
    }

    [Fact]
    public void Reads_only_the_first_period_and_counts_them()
    {
        const string mpd = """
            <MPD type="static" mediaPresentationDuration="PT30S">
              <Period id="ad" duration="PT10S"><AdaptationSet mimeType="video/mp4"><Representation id="ad" bandwidth="1"><BaseURL>ad.mp4</BaseURL></Representation></AdaptationSet></Period>
              <Period id="main" start="PT10S"><AdaptationSet mimeType="video/mp4"><Representation id="main" bandwidth="1"><BaseURL>main.mp4</BaseURL></Representation></AdaptationSet></Period>
            </MPD>
            """;
        var manifest = DashParser.Parse(mpd, ManifestUri);
        Assert.Equal(2, manifest.PeriodCount);
        Assert.Equal("ad", manifest.Representations.Single().Id);
        Assert.Equal(DashSupport.MultiPeriod, DashAnalysis.Classify(manifest));
    }

    [Theory]
    [InlineData("PT10S", 10)]
    [InlineData("PT1H2M3.5S", 3723.5)]
    [InlineData("PT0.25S", 0.25)]
    [InlineData("P1DT2H", 93600)]
    [InlineData("PT634.566S", 634.566)]
    [InlineData("PT0S", 0)]
    public void Parses_iso_8601_durations(string value, double seconds) =>
        Assert.Equal(seconds, DashParser.ParseDuration(value)!.Value, 6);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("P")]
    [InlineData("PT")]
    [InlineData("P1Y")] // years have no fixed length
    [InlineData("P2M")] // months neither
    [InlineData("10")]
    [InlineData("PT-5S")]
    [InlineData("P1DT")]
    public void Rejects_malformed_or_ambiguous_durations(string? value) =>
        Assert.Null(DashParser.ParseDuration(value));

    [Theory]
    [InlineData("$RepresentationID$/$Number$.m4s", "v1/42.m4s")]
    [InlineData("seg_$Number%05d$.m4s", "seg_00042.m4s")]
    [InlineData("t$Time$-b$Bandwidth$.mp4", "t9000-b800000.mp4")]
    [InlineData("price$$/$Number%d$", "price$/42")]
    [InlineData("plain.mp4", "plain.mp4")]
    [InlineData("$Number%2d$", "42")] // width smaller than the number: unchanged
    public void Expands_template_identifiers(string template, string expected) =>
        Assert.Equal(expected, DashParser.ExpandTemplate(template, "v1", 42, 9000, 800_000));

    [Theory]
    [InlineData("$Foo$")]
    [InlineData("$Number")]
    [InlineData("$Number%05x$")]
    [InlineData("$RepresentationID%03d$")]
    public void Refuses_bad_templates(string template) =>
        Assert.Null(DashParser.ExpandTemplate(template, "v1", 1, 0, 1));

    [Fact]
    public void Missing_representation_id_makes_the_template_unusable() =>
        Assert.Null(DashParser.ExpandTemplate("$RepresentationID$.m4s", null, 1, 0, 1));
}

public sealed class DashAnalysisTests
{
    private static readonly Uri ManifestUri = new("https://cdn.example/manifest.mpd");
    private static readonly DashSegment Piece = new(new Uri("https://cdn.example/1.m4s"), null, 4);

    private static DashRepresentation Track(
        DashTrackKind kind,
        string? codecs,
        long bandwidth,
        int? height = null,
        string? language = null,
        bool main = false,
        bool isProtected = false,
        string? mime = null,
        string? label = null,
        int? channels = null) =>
        new(null, kind, mime, codecs, bandwidth, null, height, null, language, label, channels, main, isProtected, null, [Piece]);

    private static DashManifest Manifest(params DashRepresentation[] tracks) => new(ManifestUri, false, 600, 1, tracks);

    [Fact]
    public void Classifies_drm_before_live_before_multi_period()
    {
        var video = Track(DashTrackKind.Video, "avc1", 1);
        Assert.Equal(DashSupport.Supported, DashAnalysis.Classify(Manifest(video)));
        Assert.Equal(DashSupport.Protected, DashAnalysis.Classify(Manifest(video, Track(DashTrackKind.Audio, "mp4a.40.2", 1, isProtected: true))));
        Assert.Equal(DashSupport.Live, DashAnalysis.Classify(Manifest(video) with { IsDynamic = true }));
        Assert.Equal(DashSupport.Protected, DashAnalysis.Classify(Manifest(Track(DashTrackKind.Video, "avc1", 1, isProtected: true)) with { IsDynamic = true }));
        Assert.Equal(DashSupport.MultiPeriod, DashAnalysis.Classify(Manifest(video) with { PeriodCount = 3 }));
        Assert.Equal(DashSupport.Empty, DashAnalysis.Classify(Manifest(Track(DashTrackKind.Audio, "mp4a.40.2", 1))));
        Assert.Null(DashAnalysis.Explain(DashSupport.Supported));
        Assert.Equal("Protected (DRM): can't be downloaded", DashAnalysis.Explain(DashSupport.Protected));
    }

    [Fact]
    public void Content_protection_in_a_real_manifest_is_refused()
    {
        const string mpd = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" xmlns:cenc="urn:mpeg:cenc:2013" type="static" mediaPresentationDuration="PT4S"><Period>
              <AdaptationSet mimeType="video/mp4" codecs="avc1.64001f">
                <ContentProtection schemeIdUri="urn:mpeg:dash:mp4protection:2011" value="cenc" cenc:default_KID="10000000-1000-1000-1000-100000000001" />
                <ContentProtection schemeIdUri="urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed" />
                <Representation id="v" bandwidth="1"><BaseURL>v.mp4</BaseURL></Representation>
              </AdaptationSet>
            </Period></MPD>
            """;
        Assert.Equal(DashSupport.Protected, DashAnalysis.Classify(DashParser.Parse(mpd, ManifestUri)));
    }

    [Fact]
    public void Dynamic_manifest_is_live()
    {
        const string mpd = """
            <MPD type="dynamic" availabilityStartTime="2026-01-01T00:00:00Z"><Period start="PT0S">
              <AdaptationSet mimeType="video/mp4"><Representation id="v" bandwidth="1"><SegmentTemplate duration="2" media="$Number$.m4s" /></Representation></AdaptationSet>
            </Period></MPD>
            """;
        var manifest = DashParser.Parse(mpd, ManifestUri);
        Assert.Equal(DashSupport.Live, DashAnalysis.Classify(manifest));
    }

    [Fact]
    public void Sorts_video_best_first_and_ignores_audio()
    {
        var manifest = Manifest(
            Track(DashTrackKind.Video, "avc1", 800_000, 360),
            Track(DashTrackKind.Audio, "mp4a.40.2", 128_000),
            Track(DashTrackKind.Video, "avc1", 5_000_000, 1080),
            Track(DashTrackKind.Video, "avc1", 3_000_000, 1080));
        Assert.Equal(new long[] { 5_000_000, 3_000_000, 800_000 }, DashAnalysis.SortVideoBestFirst(manifest).Select(v => v.Bandwidth));
    }

    [Fact]
    public void Picks_audio_in_the_users_language_then_highest_bitrate()
    {
        var video = Track(DashTrackKind.Video, "avc1.64001f", 1, 720);
        var english = Track(DashTrackKind.Audio, "mp4a.40.2", 96_000, language: "en");
        var englishHigh = Track(DashTrackKind.Audio, "mp4a.40.2", 192_000, language: "en-GB");
        var german = Track(DashTrackKind.Audio, "mp4a.40.2", 256_000, language: "de", main: true);
        var manifest = Manifest(video, english, englishHigh, german);

        Assert.Same(englishHigh, DashAnalysis.PickAudio(manifest, video, "en-US"));
        Assert.Same(german, DashAnalysis.PickAudio(manifest, video, "fr")); // no French: the main role
        Assert.Same(german, DashAnalysis.PickAudio(manifest, video, null));
    }

    [Fact]
    public void Prefers_audio_that_fits_the_pictures_container()
    {
        var vp9 = Track(DashTrackKind.Video, "vp09.00.40.08", 1, 1080);
        var h264 = Track(DashTrackKind.Video, "avc1.640028", 1, 1080);
        var aac = Track(DashTrackKind.Audio, "mp4a.40.2", 128_000);
        var opus = Track(DashTrackKind.Audio, "opus", 96_000);
        var manifest = Manifest(vp9, h264, aac, opus);

        Assert.Same(opus, DashAnalysis.PickAudio(manifest, vp9, null)); // WebM pair beats the higher-bitrate AAC
        Assert.Same(aac, DashAnalysis.PickAudio(manifest, h264, null));
    }

    [Fact]
    public void No_separate_audio_for_muxed_or_silent_video()
    {
        var muxed = Track(DashTrackKind.Muxed, "avc1,mp4a.40.2", 1);
        Assert.Null(DashAnalysis.PickAudio(Manifest(muxed, Track(DashTrackKind.Audio, "mp4a.40.2", 1)), muxed, "en"));
        var silent = Track(DashTrackKind.Video, "avc1", 1);
        Assert.Null(DashAnalysis.PickAudio(Manifest(silent), silent, "en"));
    }

    [Theory]
    [InlineData("avc1.640028", "mp4a.40.2", null, "MP4")]
    [InlineData("vp09.00.40.08", "opus", null, "WebM")]
    [InlineData("av01.0.08M.08", "opus", null, "WebM")]
    [InlineData("vp09.00.40.08", "mp4a.40.2", null, "MKV")]
    [InlineData(null, null, "video/webm", "WebM")]
    [InlineData(null, null, "video/mp4", "MP4")]
    public void Selects_the_container_from_both_tracks(string? videoCodecs, string? audioCodecs, string? mime, string expected)
    {
        var video = Track(DashTrackKind.Video, videoCodecs, 1, mime: mime);
        var audio = audioCodecs is null && mime is null ? null : Track(DashTrackKind.Audio, audioCodecs, 1, mime: mime?.Replace("video", "audio"));
        Assert.Equal(expected, DashAnalysis.SelectContainer(video, audio).Container);
    }

    [Fact]
    public void Labels_come_from_data_only()
    {
        var video = Track(DashTrackKind.Video, "avc1.640028", 4_800_000, 1080);
        var audio = Track(DashTrackKind.Audio, "mp4a.40.2", 128_000, language: "en", channels: 2);
        Assert.Equal("1080p · 4.8 Mbps · H.264 · ≈ 352 MB est.", DashAnalysis.VideoLabel(video, 600, audio, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("1080p · 4.8 Mbps · H.264", DashAnalysis.VideoLabel(video, null));
        Assert.Equal("Unknown quality", DashAnalysis.VideoLabel(Track(DashTrackKind.Video, null, 0), 600));
        Assert.Equal("1080p · 60fps · 4.8 Mbps · H.264", DashAnalysis.VideoLabel(video with { FrameRate = 59.94 }, null));

        Assert.Equal("English · AAC · 128 kbps · stereo", DashAnalysis.AudioLabel(audio));
        Assert.Equal("Director's commentary · AAC · 128 kbps · 5.1", DashAnalysis.AudioLabel(audio with { Label = "Director's commentary", AudioChannels = 6 }));
        Assert.Equal("Audio", DashAnalysis.AudioLabel(Track(DashTrackKind.Audio, null, 0)));
    }

    [Theory]
    [InlineData("en", "English")]
    [InlineData("de-DE", "German")]
    [InlineData("und", null)]
    [InlineData("qaa", null)] // private use (EnvivioDash3 test stream)
    [InlineData("qtz", null)]
    [InlineData(null, null)]
    public void Names_languages(string? tag, string? expected) =>
        Assert.Equal(expected, DashAnalysis.LanguageName(tag));

    [Fact]
    public void Duration_falls_back_to_the_pieces()
    {
        var video = Track(DashTrackKind.Video, "avc1", 1);
        Assert.Equal(600, DashAnalysis.Duration(Manifest(video)));
        Assert.Equal(4, DashAnalysis.Duration(Manifest(video) with { DurationSeconds = null }));
    }
}
