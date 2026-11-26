using System.Globalization;
using System.Security.Cryptography;
using AJBrowser.Core.Media.Hls;

namespace AJBrowser.Core.Tests.Media;

public sealed class HlsParserTests
{
    private static readonly Uri MasterUri = new("https://cdn.example/videos/show/master.m3u8?token=abc");

    private const string Master = """
        #EXTM3U
        #EXT-X-VERSION:6
        #EXT-X-INDEPENDENT-SEGMENTS
        #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aud",NAME="English",LANGUAGE="en",DEFAULT=YES,AUTOSELECT=YES,URI="audio/en/prog.m3u8"
        #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="aud",NAME="Español",LANGUAGE="es",DEFAULT=NO,URI="audio/es/prog.m3u8"
        #EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID="subs",NAME="English",LANGUAGE="en",URI="subs/en.m3u8"
        #EXT-X-STREAM-INF:BANDWIDTH=5200000,AVERAGE-BANDWIDTH=4800000,RESOLUTION=1920x1080,CODECS="avc1.640028,mp4a.40.2",FRAME-RATE=29.970,AUDIO="aud",SUBTITLES="subs"
        v1080/prog.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360,CODECS="avc1.4d401e,mp4a.40.2",AUDIO="aud"
        https://other-cdn.example/v360/prog.m3u8
        #EXT-X-I-FRAME-STREAM-INF:BANDWIDTH=200000,RESOLUTION=1920x1080,URI="v1080/iframes.m3u8"
        #EXT-X-STREAM-INF:BANDWIDTH=2500000
        /abs/v720/prog.m3u8
        """;

    [Fact]
    public void Parses_variants_with_their_attributes_and_resolved_addresses()
    {
        var master = Assert.IsType<HlsMasterPlaylist>(HlsParser.Parse(Master, MasterUri));
        Assert.Equal(3, master.Variants.Count); // the I-frame stream is ignored

        var best = master.Variants[0];
        Assert.Equal("https://cdn.example/videos/show/v1080/prog.m3u8", best.Uri.AbsoluteUri);
        Assert.Equal((5_200_000L, 4_800_000L, 1920, 1080), (best.Bandwidth, best.AverageBandwidth!.Value, best.Width!.Value, best.Height!.Value));
        Assert.Equal("avc1.640028,mp4a.40.2", best.Codecs); // quoted value with a comma inside
        Assert.Equal(29.97, best.FrameRate!.Value, 2);
        Assert.Equal(("aud", "subs"), (best.AudioGroup, best.SubtitlesGroup));

        Assert.Equal("https://other-cdn.example/v360/prog.m3u8", master.Variants[1].Uri.AbsoluteUri);
        Assert.Equal("https://cdn.example/abs/v720/prog.m3u8", master.Variants[2].Uri.AbsoluteUri);
        Assert.Null(master.Variants[2].Height); // no RESOLUTION: bitrate only
    }

    [Fact]
    public void Parses_audio_and_subtitle_renditions()
    {
        var master = (HlsMasterPlaylist)HlsParser.Parse(Master, MasterUri);
        Assert.Equal(3, master.Renditions.Count);
        var english = master.Renditions[0];
        Assert.Equal(("AUDIO", "aud", "English", "en", true), (english.Type, english.GroupId, english.Name, english.Language, english.IsDefault));
        Assert.Equal("https://cdn.example/videos/show/audio/en/prog.m3u8", english.Uri!.AbsoluteUri);
        Assert.Equal("Español", master.Renditions[1].Name);
        Assert.Equal("SUBTITLES", master.Renditions[2].Type);
    }

    [Fact]
    public void Parses_a_ts_media_playlist()
    {
        const string text = "﻿#EXTM3U\r\n#EXT-X-VERSION:3\r\n#EXT-X-TARGETDURATION:10\r\n#EXT-X-MEDIA-SEQUENCE:42\r\n#EXT-X-PLAYLIST-TYPE:VOD\r\n" +
                            "#EXTINF:9.009,\r\nseg42.ts\r\n#EXTINF:9.009,title here\r\nseg43.ts\r\n\r\n#EXTINF:3.5,\r\nseg44.ts\r\n#EXT-X-ENDLIST\r\n";
        var media = Assert.IsType<HlsMediaPlaylist>(HlsParser.Parse(text, new Uri("https://cdn.example/v/prog.m3u8")));

        Assert.Equal(3, media.Segments.Count);
        Assert.Equal([42L, 43L, 44L], media.Segments.Select(s => s.SequenceNumber));
        Assert.Equal("https://cdn.example/v/seg43.ts", media.Segments[1].Uri.AbsoluteUri);
        Assert.Equal(21.518, media.TotalDurationSeconds, 3);
        Assert.True(media.IsEndList);
        Assert.False(media.IsLive);
        Assert.False(media.IsFragmentedMp4);
        Assert.Equal(HlsSupport.Supported, HlsAnalysis.Classify(media));
    }

    [Fact]
    public void Parses_fmp4_with_map_and_byte_ranges()
    {
        // Apple-style fMP4 HLS: one big main.mp4, sliced by byte ranges (spike F1).
        const string text = """
            #EXTM3U
            #EXT-X-TARGETDURATION:6
            #EXT-X-MAP:URI="main.mp4",BYTERANGE="720@0"
            #EXTINF:6.0,
            #EXT-X-BYTERANGE:1000@720
            main.mp4
            #EXTINF:6.0,
            #EXT-X-BYTERANGE:2000
            main.mp4
            #EXTINF:2.0,
            #EXT-X-BYTERANGE:500@5000
            main.mp4
            #EXT-X-ENDLIST
            """;
        var media = (HlsMediaPlaylist)HlsParser.Parse(text, new Uri("https://cdn.example/f/prog.m3u8"));

        Assert.True(media.IsFragmentedMp4);
        Assert.Equal(new HlsByteRange(720, 0), media.Segments[0].Map!.ByteRange);
        Assert.Equal(new HlsByteRange(1000, 720), media.Segments[0].ByteRange);
        Assert.Equal(new HlsByteRange(2000, 1720), media.Segments[1].ByteRange); // continues after the previous range
        Assert.Equal(new HlsByteRange(500, 5000), media.Segments[2].ByteRange);
        Assert.Equal("bytes=720-1719", media.Segments[0].ByteRange!.ToRangeHeader());
    }

    [Fact]
    public void Tracks_keys_discontinuities_and_key_changes()
    {
        const string text = """
            #EXTM3U
            #EXT-X-TARGETDURATION:4
            #EXT-X-MEDIA-SEQUENCE:7
            #EXT-X-KEY:METHOD=AES-128,URI="https://keys.example/k1",IV=0x000102030405060708090A0B0C0D0E0F
            #EXTINF:4,
            a.ts
            #EXT-X-KEY:METHOD=AES-128,URI="k2.bin"
            #EXTINF:4,
            b.ts
            #EXT-X-DISCONTINUITY
            #EXT-X-KEY:METHOD=NONE
            #EXTINF:4,
            ad.ts
            #EXT-X-ENDLIST
            """;
        var media = (HlsMediaPlaylist)HlsParser.Parse(text, new Uri("https://cdn.example/e/prog.m3u8"));

        Assert.Equal("https://keys.example/k1", media.Segments[0].Key!.Uri!.AbsoluteUri);
        Assert.Equal(Convert.FromHexString("000102030405060708090A0B0C0D0E0F"), media.Segments[0].Key!.IV);
        Assert.Equal("https://cdn.example/e/k2.bin", media.Segments[1].Key!.Uri!.AbsoluteUri);
        Assert.Null(media.Segments[1].Key!.IV);
        Assert.Null(media.Segments[2].Key); // METHOD=NONE
        Assert.Equal([false, false, true], media.Segments.Select(s => s.DiscontinuityBefore));
        Assert.Equal(HlsSupport.Supported, HlsAnalysis.Classify(media)); // AES-128 is standard encryption, not DRM
    }

    [Theory]
    [InlineData("#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://key\",KEYFORMAT=\"com.apple.streamingkeydelivery\"")]
    [InlineData("#EXT-X-KEY:METHOD=SAMPLE-AES-CTR,URI=\"data:text/plain;base64,AAAA\",KEYFORMAT=\"urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed\"")]
    [InlineData("#EXT-X-KEY:METHOD=AES-128,URI=\"https://k.example/k\",KEYFORMAT=\"com.microsoft.playready\"")]
    public void Drm_streams_are_protected(string keyLine)
    {
        var text = $"#EXTM3U\n#EXT-X-TARGETDURATION:4\n{keyLine}\n#EXTINF:4,\na.ts\n#EXT-X-ENDLIST\n";
        Assert.Equal(HlsSupport.Protected, HlsAnalysis.Classify((HlsMediaPlaylist)HlsParser.Parse(text, new Uri("https://cdn.example/p.m3u8"))));
    }

    [Fact]
    public void Live_streams_are_recognised()
    {
        const string live = "#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:1000\n#EXTINF:4,\na.ts\n#EXTINF:4,\nb.ts\n";
        const string evt = "#EXTM3U\n#EXT-X-PLAYLIST-TYPE:EVENT\n#EXTINF:4,\na.ts\n";
        const string vodWithoutEnd = "#EXTM3U\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXTINF:4,\na.ts\n";
        var uri = new Uri("https://cdn.example/live.m3u8");

        Assert.Equal(HlsSupport.Live, HlsAnalysis.Classify((HlsMediaPlaylist)HlsParser.Parse(live, uri)));
        Assert.Equal(HlsSupport.Live, HlsAnalysis.Classify((HlsMediaPlaylist)HlsParser.Parse(evt, uri)));
        Assert.Equal(HlsSupport.Supported, HlsAnalysis.Classify((HlsMediaPlaylist)HlsParser.Parse(vodWithoutEnd, uri)));
    }

    [Fact]
    public void Session_keys_mark_a_master_as_protected()
    {
        var text = Master.Replace("#EXT-X-INDEPENDENT-SEGMENTS", "#EXT-X-SESSION-KEY:METHOD=SAMPLE-AES,URI=\"skd://x\",KEYFORMAT=\"com.apple.streamingkeydelivery\"");
        Assert.True(HlsAnalysis.IsProtected((HlsMasterPlaylist)HlsParser.Parse(text, MasterUri)));
        Assert.False(HlsAnalysis.IsProtected((HlsMasterPlaylist)HlsParser.Parse(Master, MasterUri)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>not a playlist</html>")]
    [InlineData("EXTM3U\n#EXTINF:4,\na.ts")]
    public void Rejects_non_playlists(string text)
    {
        Assert.Throws<FormatException>(() => HlsParser.Parse(text, MasterUri));
        Assert.Null(HlsParser.TryParse(text, MasterUri));
    }

    [Fact]
    public void Ignores_non_http_and_orphan_addresses()
    {
        const string text = "#EXTM3U\n#EXTINF:4,\njavascript:alert(1)\norphan.ts\n#EXTINF:4,\nok.ts\n#EXT-X-ENDLIST\n";
        var media = (HlsMediaPlaylist)HlsParser.Parse(text, new Uri("https://cdn.example/x.m3u8"));
        Assert.Equal("https://cdn.example/ok.ts", Assert.Single(media.Segments).Uri.AbsoluteUri);
    }

    [Fact]
    public void Attribute_lists_handle_quotes_commas_and_case()
    {
        var a = HlsParser.Attributes("#EXT-X-STREAM-INF:bandwidth=100,CODECS=\"a,b\",RESOLUTION=1x2,NAME=\"x\"");
        Assert.Equal("100", a["BANDWIDTH"]);
        Assert.Equal("a,b", a["CODECS"]);
        Assert.Equal("1x2", a["RESOLUTION"]);
        Assert.Equal("x", a["NAME"]);
    }
}

public sealed class HlsAnalysisTests
{
    [Theory]
    [InlineData("avc1.640028,mp4a.40.2", "H.264/AAC")]
    [InlineData("hvc1.2.4.L123.B0,ec-3", "H.265/E-AC-3")]
    [InlineData("av01.0.08M.08,opus", "AV1/Opus")]
    [InlineData("vp09.00.40.08", "VP9")]
    [InlineData("mp4a.40.5", "AAC")]
    [InlineData("avc1.4d401e,avc1.640028,mp4a.40.2", "H.264/AAC")]
    [InlineData("weird.1,mp4a.40.2", "weird.1/AAC")]
    public void Names_codecs(string codecs, string expected) => Assert.Equal(expected, HlsAnalysis.CodecLabel(codecs));

    [Fact]
    public void No_codecs_gives_no_label() => Assert.Null(HlsAnalysis.CodecLabel(null));

    [Theory]
    [InlineData("avc1.640028,mp4a.40.2", "MP4")]
    [InlineData("hvc1.1,ac-3", "MP4")]
    [InlineData("vp09.00.40.08,opus", "WebM")]
    [InlineData("vp09.00.40.08,mp4a.40.2", "MKV")]
    [InlineData("avc1.640028,opus", "MKV")]
    [InlineData(null, "MP4")]
    public void Selects_a_container(string? codecs, string expected)
    {
        var (container, reason) = HlsAnalysis.SelectContainer(codecs);
        Assert.Equal(expected, container);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void Labels_variants_from_data_only()
    {
        var full = new HlsVariant(new Uri("https://a.example/1.m3u8"), 5_200_000, 4_800_000, 1920, 1080, "avc1.640028,mp4a.40.2", 30, null, null);
        Assert.Equal("1080p · 4.8 Mbps · H.264/AAC · ≈ 687 MB est.", HlsAnalysis.VariantLabel(full, 1200, CultureInfo.InvariantCulture));
        Assert.Equal("1080p · 4.8 Mbps · H.264/AAC", HlsAnalysis.VariantLabel(full, null));

        var bare = new HlsVariant(new Uri("https://a.example/2.m3u8"), 800_000, null, null, null, null, null, null, null);
        Assert.Equal("800 kbps", HlsAnalysis.VariantLabel(bare, null));
    }

    [Fact]
    public void Sorts_best_quality_first()
    {
        HlsVariant V(int? h, long bw) => new(new Uri($"https://a.example/{h}-{bw}.m3u8"), bw, null, null, h, null, null, null, null);
        var sorted = HlsAnalysis.SortBestFirst([V(360, 800_000), V(1080, 5_000_000), V(null, 9_000_000), V(1080, 3_000_000)]);
        Assert.Equal([(1080, 5_000_000L), (1080, 3_000_000L), (360, 800_000L), (0, 9_000_000L)], sorted.Select(v => (v.Height ?? 0, v.Bandwidth)));
    }

    [Theory]
    [InlineData(5_200_000, "5.2 Mbps")]
    [InlineData(800_000, "800 kbps")]
    [InlineData(500, "1 kbps")]
    public void Formats_bitrates(long bps, string expected) => Assert.Equal(expected, HlsAnalysis.Bitrate(bps));
}

public sealed class HlsDecryptorTests
{
    [Fact]
    public void Iv_defaults_to_the_sequence_number_big_endian()
    {
        var segment = new HlsSegment(new Uri("https://a.example/s.ts"), 4, 0x0102030405, null, new HlsKey("AES-128", new Uri("https://a.example/k"), null, null), null, false);
        Assert.Equal(Convert.FromHexString("00000000000000000000000102030405"), HlsDecryptor.IvFor(segment));

        var withIv = segment with { Key = segment.Key! with { IV = Convert.FromHexString("0F0E0D0C0B0A09080706050403020100") } };
        Assert.Equal(Convert.FromHexString("0F0E0D0C0B0A09080706050403020100"), HlsDecryptor.IvFor(withIv));
    }

    [Fact]
    public void Matches_the_nist_aes128_cbc_vector_and_round_trips()
    {
        // NIST SP 800-38A F.2.1 (AES-128-CBC), first block.
        var key = Convert.FromHexString("2b7e151628aed2a6abf7158809cf4f3c");
        var iv = Convert.FromHexString("000102030405060708090a0b0c0d0e0f");
        var plain = Convert.FromHexString("6bc1bee22e409f96e93d7e117393172a");

        using var aes = Aes.Create();
        aes.Key = key;
        var encrypted = aes.EncryptCbc(plain, iv, PaddingMode.PKCS7);
        Assert.Equal(Convert.FromHexString("7649abac8119b246cee98e9b12e9197d"), encrypted[..16]);

        Assert.Equal(plain, HlsDecryptor.Decrypt(encrypted, key, iv));
    }

    [Fact]
    public void Wrong_key_length_is_refused() =>
        Assert.Throws<CryptographicException>(() => HlsDecryptor.Decrypt(new byte[16], new byte[8], new byte[16]));
}
