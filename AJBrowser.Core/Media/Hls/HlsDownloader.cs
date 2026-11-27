using System.Net;
using System.Text;
using System.Text.Json;
using AJBrowser.Core.Downloads;
using AJBrowser.Core.Media.Ffmpeg;

namespace AJBrowser.Core.Media.Hls;

/// <summary>What <see cref="HlsDownloader.PrepareAsync"/> found out before a stream download is queued.</summary>
public sealed record HlsPlan(
    HlsSupport Support,
    Uri? MediaPlaylistUrl,
    HlsVariant? Variant,
    HlsMediaPlaylist? Media,
    string OutputExtension,
    bool HasSeparateAudio,
    string? Problem,
    Uri? AudioPlaylistUrl = null,
    string? AudioName = null)
{
    public bool CanDownload => Support == HlsSupport.Supported && MediaPlaylistUrl is not null && Media is not null;

    /// <summary>Bitrate × duration, when both are known (always an estimate).</summary>
    public long? EstimatedBytes => Variant is { } v && Media is { } m ? HlsAnalysis.EstimateBytes(v.EffectiveBandwidth, m.TotalDurationSeconds) : null;
}

/// <summary>
/// What a stream offers, for the picker (Phase 6c): its qualities best first, its length (from one quality's piece
/// list; the same for all of them), whether it can be downloaded, and the playlists the master refers to (so the page's
/// own requests for those don't show up as extra rows).
/// </summary>
public sealed record HlsInspection(
    HlsSupport Support,
    string? Problem,
    IReadOnlyList<HlsVariant> Variants,
    double? DurationSeconds,
    IReadOnlyList<Uri> ReferencedPlaylists,
    bool HasSeparateAudio);

/// <summary>
/// Downloads an unprotected, finished (VOD) HLS stream into one file (plan Phase 6):
/// <list type="bullet">
/// <item>Pieces (segments) are fetched <see cref="Parallelism"/> at a time with the page's allowlisted headers, each
/// retried on network trouble, AES-128 ones decrypted, byte-range slices honoured. Each finished piece is saved in a
/// temporary folder per job and track, so pausing or an app restart continues where it stopped.</item>
/// <item>The <b>Cookie</b> header (fetched for the playlist's address) is sent only to that same host: pieces often
/// come from other CDN hosts, which must not receive the site's cookies.</item>
/// <item>Assembly: fMP4 streams (init section + pieces) are already a valid MP4. TS streams become MP4 through FFmpeg
/// when it's available (local files in and out: FFmpeg never touches the network or cookies), otherwise .ts.</item>
/// <item>A separate sound track (<see cref="DownloadRequest.AudioUrl"/>, only queued when FFmpeg is there) is
/// downloaded the same way and merged with FFmpeg; if FFmpeg is gone by then, the picture is saved on its own.</item>
/// </list>
/// </summary>
public sealed class HlsDownloader : IStreamDownloader
{
    private const string JobFile = "job.json";

    private readonly HttpClient _http;
    private readonly string _tempRoot;
    private readonly Func<string?> _ffmpegPath;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="tempRoot">Where each job keeps its pieces (<c>%LOCALAPPDATA%\AJBrowser\Temp\hls</c>).</param>
    /// <param name="ffmpegPath">The FFmpeg to use right now, or null (asked at the moment it's needed).</param>
    public HlsDownloader(HttpClient http, string tempRoot, Func<string?> ffmpegPath, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _tempRoot = tempRoot;
        _ffmpegPath = ffmpegPath;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Pieces fetched at the same time.</summary>
    public int Parallelism { get; init; } = 4;

    /// <summary>Extra attempts per piece for network errors, timeouts, 408/429/5xx.</summary>
    public int MaxRetries { get; init; } = 3;

    public string TempFolderFor(long jobId) => Path.Combine(_tempRoot, jobId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public void DeleteTemp(long jobId)
    {
        try
        {
            var folder = TempFolderFor(jobId);
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In use or gone; the startup cleanup will get it later.
        }
    }

    // ---------------------------------------------------------------- before queueing

    /// <summary>
    /// Reads a stream's playlist for the picker: the qualities (master playlist) and the length, by fetching the best
    /// quality's piece list. Never throws for network or format problems: they come back as a problem text.
    /// </summary>
    public async Task<HlsInspection> InspectAsync(Uri playlistUrl, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken = default)
    {
        try
        {
            var playlist = HlsParser.TryParse(await GetTextAsync(playlistUrl, headers, playlistUrl, cancellationToken).ConfigureAwait(false), playlistUrl);
            switch (playlist)
            {
                case HlsMediaPlaylist media:
                {
                    var support = HlsAnalysis.Classify(media);
                    return new HlsInspection(support, HlsAnalysis.Explain(support), [], media.TotalDurationSeconds, [], false);
                }

                case HlsMasterPlaylist master:
                {
                    var referenced = master.Variants.Select(v => v.Uri).Concat(master.Renditions.Select(r => r.Uri).OfType<Uri>()).Distinct().ToList();
                    var separateAudio = master.Variants.Any(v => AudioRenditionFor(master, v) is not null);
                    if (HlsAnalysis.IsProtected(master))
                    {
                        return new HlsInspection(HlsSupport.Protected, HlsAnalysis.Explain(HlsSupport.Protected), [], null, referenced, separateAudio);
                    }

                    var variants = HlsAnalysis.SortBestFirst(master.Variants);
                    if (variants.Count == 0)
                    {
                        return new HlsInspection(HlsSupport.Empty, HlsAnalysis.Explain(HlsSupport.Empty), [], null, referenced, false);
                    }

                    // One quality's piece list tells the length (and DRM keys or "live", which apply to all of them).
                    var best = variants[0];
                    var media = HlsParser.TryParse(await GetTextAsync(best.Uri, headers, playlistUrl, cancellationToken).ConfigureAwait(false), best.Uri) as HlsMediaPlaylist;
                    var support = media is null ? HlsSupport.Empty : HlsAnalysis.Classify(media);
                    var problem = media is null ? "The stream's playlist couldn't be read." : HlsAnalysis.Explain(support);
                    return new HlsInspection(support, problem, variants, media?.TotalDurationSeconds, referenced, separateAudio);
                }

                default:
                    return new HlsInspection(HlsSupport.Empty, "The stream's playlist couldn't be read.", [], null, [], false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is StreamFatalException or HttpRequestException or TaskCanceledException or IOException)
        {
            return new HlsInspection(HlsSupport.Empty, ex is StreamFatalException ? ex.Message : "The stream's playlist couldn't be read.", [], null, [], false);
        }
    }

    /// <summary>
    /// Fetches the playlist; for a master playlist picks a quality (<paramref name="variantUri"/>, else the best) and
    /// fetches its media playlist. Decides whether it can be downloaded, which file extension the result gets, and, when
    /// FFmpeg is available, which separate sound track to merge in.
    /// </summary>
    /// <param name="audioGroup">With <paramref name="variantUri"/>: the chosen quality's sound group. Several qualities can share
    /// one picture playlist and differ only in sound (Apple's example: 270p with AAC, AC-3 or E-AC-3); without it the first one wins.</param>
    /// <param name="bandwidth">With <paramref name="variantUri"/>: the chosen quality's bitrate (same reason).</param>
    public async Task<HlsPlan> PrepareAsync(
        Uri playlistUrl,
        IReadOnlyDictionary<string, string>? headers,
        Uri? variantUri = null,
        CancellationToken cancellationToken = default,
        string? audioGroup = null,
        long? bandwidth = null)
    {
        var playlist = HlsParser.TryParse(await GetTextAsync(playlistUrl, headers, playlistUrl, cancellationToken).ConfigureAwait(false), playlistUrl);
        HlsVariant? variant = null;
        HlsRendition? audio = null;
        var mediaUrl = playlistUrl;

        if (playlist is HlsMasterPlaylist master)
        {
            if (HlsAnalysis.IsProtected(master))
            {
                return Plan(HlsSupport.Protected);
            }

            var sorted = HlsAnalysis.SortBestFirst(master.Variants);
            variant = sorted.FirstOrDefault(v => variantUri is not null && v.Uri == variantUri
                                                 && (audioGroup is null || v.AudioGroup == audioGroup)
                                                 && (bandwidth is null || v.Bandwidth == bandwidth))
                      ?? sorted.FirstOrDefault(v => variantUri is not null && v.Uri == variantUri)
                      ?? sorted.FirstOrDefault();
            if (variant is null)
            {
                return Plan(HlsSupport.Empty);
            }

            audio = AudioRenditionFor(master, variant);
            mediaUrl = variant.Uri;
            playlist = HlsParser.TryParse(await GetTextAsync(mediaUrl, headers, playlistUrl, cancellationToken).ConfigureAwait(false), mediaUrl);
        }

        if (playlist is not HlsMediaPlaylist media)
        {
            return Plan(HlsSupport.Empty) with { Problem = "The stream's playlist couldn't be read." };
        }

        var support = HlsAnalysis.Classify(media);
        var ffmpeg = _ffmpegPath() is not null;
        var mergeAudio = audio is not null && ffmpeg;
        var (container, _) = HlsAnalysis.SelectContainer(variant?.Codecs);
        var extension = mergeAudio || (!media.IsFragmentedMp4 && ffmpeg)
            ? container switch { "WebM" => ".webm", "MKV" => ".mkv", _ => ".mp4" }
            : media.IsFragmentedMp4 ? ".mp4" : ".ts";
        return new HlsPlan(support, mediaUrl, variant, media, extension, audio is not null, HlsAnalysis.Explain(support),
            mergeAudio ? audio!.Uri : null, audio?.Name);

        static HlsPlan Plan(HlsSupport support) => new(support, null, null, null, ".mp4", false, HlsAnalysis.Explain(support));
    }

    /// <summary>The sound track a quality uses when it's separate: the group's default, else its first track with a playlist.</summary>
    private static HlsRendition? AudioRenditionFor(HlsMasterPlaylist master, HlsVariant variant)
    {
        if (variant.AudioGroup is not { } group)
        {
            return null;
        }

        var tracks = master.Renditions.Where(r => r.Type == "AUDIO" && r.GroupId == group && r.Uri is not null).ToList();
        return tracks.FirstOrDefault(r => r.IsDefault) ?? tracks.FirstOrDefault();
    }

    // ---------------------------------------------------------------- the download

    /// <summary>
    /// Downloads the stream whose <b>media</b> playlist is <see cref="DownloadRequest.Url"/> (plus the sound track at
    /// <see cref="DownloadRequest.AudioUrl"/>, if any) into <see cref="DownloadRequest.TargetPath"/>. Never throws for
    /// network, HTTP or file problems (they come back as Failed). Cancellation keeps the finished pieces (pause);
    /// <see cref="DeleteTemp"/> removes them (cancel).
    /// </summary>
    public Task<DownloadResult> DownloadAsync(long jobId, DownloadRequest request, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken) =>
        DownloadTracksAsync(jobId, request, async token =>
        {
            var tracks = new List<StreamTrack> { StreamTrack.FromHls(await ReadMediaAsync(request.Url, request, token).ConfigureAwait(false)) };
            if (request.AudioUrl is { } audioUrl)
            {
                tracks.Add(StreamTrack.FromHls(await ReadMediaAsync(audioUrl, request, token).ConfigureAwait(false)));
            }

            return tracks;
        }, progress, cancellationToken);

    /// <summary>
    /// The piece engine shared by HLS and DASH: <paramref name="load"/> returns the picture track and optionally a sound
    /// track; their pieces are fetched (resumably, in the job's temp folder), joined, and merged/remuxed into
    /// <see cref="DownloadRequest.TargetPath"/>. Throw <see cref="StreamFatalException"/> from <paramref name="load"/>
    /// for a problem to show. Never throws for network, HTTP or file problems.
    /// </summary>
    internal async Task<DownloadResult> DownloadTracksAsync(
        long jobId,
        DownloadRequest request,
        Func<CancellationToken, Task<IReadOnlyList<StreamTrack>>> load,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var folder = TempFolderFor(jobId);
        var tracks = new List<Track>();
        try
        {
            var loaded = await load(cancellationToken).ConfigureAwait(false);
            string[] names = ["video", "audio"];
            for (var i = 0; i < loaded.Count && i < names.Length; i++)
            {
                tracks.Add(Track.Open(Path.Combine(folder, names[i]), loaded[i]));
            }

            void Report()
            {
                var received = tracks.Sum(t => t.Received);
                var estimates = tracks.Select(t => t.EstimatedTotal).ToList();
                progress?.Report(new DownloadProgress(received, estimates.All(e => e is not null) ? estimates.Sum(e => e!.Value) : null));
            }

            Report();
            foreach (var track in tracks)
            {
                await DownloadTrackAsync(track, request, Report, cancellationToken).ConfigureAwait(false);
            }

            var finalPath = await AssembleAsync(tracks, request.TargetPath, cancellationToken).ConfigureAwait(false);
            DeleteTemp(jobId);
            var size = new FileInfo(finalPath).Length;
            progress?.Report(new DownloadProgress(size, size));
            return new DownloadResult(DownloadOutcome.Completed, size, size, null, null, finalPath == request.TargetPath ? null : finalPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new DownloadResult(DownloadOutcome.Cancelled, tracks.Sum(t => t.Received), null, null); // pieces kept for resuming
        }
        catch (StreamFatalException ex)
        {
            return Failed(ex.Message);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return Failed("A piece couldn't be decrypted (wrong or expired key). Play the video again, then Retry.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed($"Couldn't write the file: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            return Failed($"Network error: {ex.HttpRequestError}");
        }

        static DownloadResult Failed(string message) => new(DownloadOutcome.Failed, 0, null, null, message);
    }

    private async Task<HlsMediaPlaylist> ReadMediaAsync(Uri url, DownloadRequest request, CancellationToken cancellationToken)
    {
        var text = await GetTextAsync(url, request.Headers, request.Url, cancellationToken).ConfigureAwait(false);
        if (HlsParser.TryParse(text, url) is not HlsMediaPlaylist media)
        {
            throw new StreamFatalException("The stream's playlist couldn't be read.");
        }

        return HlsAnalysis.Explain(HlsAnalysis.Classify(media)) is { } problem ? throw new StreamFatalException(problem) : media;
    }

    private async Task DownloadTrackAsync(Track track, DownloadRequest request, Action report, CancellationToken cancellationToken)
    {
        // Keys and init sections are small and shared by many pieces: fetch each once.
        var keys = new Dictionary<Uri, byte[]>();
        var keyLock = new SemaphoreSlim(1, 1);
        var maps = DistinctMaps(track.Media);
        for (var i = 0; i < maps.Count; i++)
        {
            if (!File.Exists(MapPath(track.Folder, i)))
            {
                var data = await FetchWithRetriesAsync(maps[i].Uri, maps[i].ByteRange, request, cancellationToken).ConfigureAwait(false);
                await File.WriteAllBytesAsync(MapPath(track.Folder, i), data, cancellationToken).ConfigureAwait(false);
            }
        }

        var pending = Enumerable.Range(0, track.Media.Segments.Count).Where(i => !File.Exists(SegmentPath(track.Folder, i))).ToList();
        await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Parallelism), CancellationToken = cancellationToken }, async (index, token) =>
        {
            var segment = track.Media.Segments[index];
            var data = await FetchWithRetriesAsync(segment.Uri, segment.ByteRange, request, token).ConfigureAwait(false);
            if (segment.Key is { IsAes128: true } key)
            {
                var keyBytes = await GetKeyAsync(key, request, keys, keyLock, token).ConfigureAwait(false);
                data = HlsDecryptor.Decrypt(data, keyBytes, HlsDecryptor.IvFor(segment));
            }

            var final = SegmentPath(track.Folder, index);
            await File.WriteAllBytesAsync(final + ".part", data, token).ConfigureAwait(false);
            File.Move(final + ".part", final, overwrite: true);
            track.Completed(index, data.LongLength);
            report();
        }).ConfigureAwait(false);
    }

    /// <summary>One playlist's pieces in its own folder, with what's already done.</summary>
    private sealed class Track
    {
        private readonly long[] _sizes;
        private int _done;

        private Track(string folder, StreamTrack stream)
        {
            Folder = folder;
            Media = stream.Media;
            Extension = stream.Extension;
            var media = stream.Media;
            _sizes = new long[media.Segments.Count];
            for (var i = 0; i < _sizes.Length; i++)
            {
                if (File.Exists(SegmentPath(folder, i)))
                {
                    _sizes[i] = new FileInfo(SegmentPath(folder, i)).Length;
                    _done++;
                }
            }
        }

        public string Folder { get; }
        public HlsMediaPlaylist Media { get; }

        /// <summary>What the joined pieces form: ".ts", ".mp4" or ".webm".</summary>
        public string Extension { get; }

        public bool IsTransportStream => Extension == ".ts";

        public long Received => _sizes.Sum();

        /// <summary>Average finished piece × number of pieces (an estimate until the end).</summary>
        public long? EstimatedTotal
        {
            get
            {
                var done = Volatile.Read(ref _done);
                return done > 0 ? (long)((double)Received / done * _sizes.Length) : null;
            }
        }

        /// <summary>Keeps finished pieces from an earlier run only if the playlist still has the same pieces.</summary>
        public static Track Open(string folder, StreamTrack stream)
        {
            PrepareFolder(folder, stream.Media);
            return new Track(folder, stream);
        }

        public void Completed(int index, long bytes)
        {
            _sizes[index] = bytes;
            Interlocked.Increment(ref _done);
        }
    }

    // ---------------------------------------------------------------- temp folder

    private static string SegmentPath(string folder, int index) => Path.Combine(folder, $"seg-{index:D6}.bin");

    private static string MapPath(string folder, int index) => Path.Combine(folder, $"map-{index:D3}.bin");

    /// <summary>
    /// Keeps finished pieces from an earlier run only if the playlist still has the same pieces (count and durations);
    /// tokens in their addresses may differ, which is fine.
    /// </summary>
    private static void PrepareFolder(string folder, HlsMediaPlaylist media)
    {
        var signature = $"{media.Segments.Count}:{Math.Round(media.TotalDurationSeconds, 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        var jobFile = Path.Combine(folder, JobFile);
        if (Directory.Exists(folder))
        {
            var previous = File.Exists(jobFile) ? ReadSignature(jobFile) : null;
            if (previous != signature)
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        Directory.CreateDirectory(folder);
        File.WriteAllText(jobFile, JsonSerializer.Serialize(new { signature }));
    }

    private static string? ReadSignature(string jobFile)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(jobFile));
            return document.RootElement.TryGetProperty("signature", out var value) ? value.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private static List<HlsMap> DistinctMaps(HlsMediaPlaylist media) =>
        media.Segments.Select(s => s.Map).OfType<HlsMap>().Distinct().ToList();

    // ---------------------------------------------------------------- assembly

    /// <summary>
    /// Picture and sound: join each track, merge them with FFmpeg. Picture only (or FFmpeg missing or failing):
    /// fMP4 is used as it is, TS is remuxed when FFmpeg is there, else kept as .ts.
    /// </summary>
    private async Task<string> AssembleAsync(List<Track> tracks, string targetPath, CancellationToken cancellationToken)
    {
        var video = tracks[0];
        var container = Path.GetExtension(targetPath).ToLowerInvariant() switch { ".webm" => "WebM", ".mkv" => "MKV", _ => "MP4" };
        var ffmpeg = _ffmpegPath();

        if (tracks.Count > 1 && ffmpeg is not null)
        {
            var videoFile = Path.Combine(video.Folder, "joined" + video.Extension);
            var audio = tracks[1];
            var audioFile = Path.Combine(audio.Folder, "joined" + audio.Extension);
            await JoinAsync(video, videoFile, cancellationToken).ConfigureAwait(false);
            await JoinAsync(audio, audioFile, cancellationToken).ConfigureAwait(false);

            var output = FileNamer.PartialPathFor(targetPath);
            var result = await FfmpegRunner.RunAsync(ffmpeg, FfmpegRunner.MergeArguments(videoFile, audioFile, output, container), video.Media.TotalDurationSeconds, null, cancellationToken).ConfigureAwait(false);
            if (result.Succeeded && File.Exists(output))
            {
                return MoveIntoPlace(output, targetPath);
            }

            DeleteQuietly(output);
            return await AssembleVideoOnlyAsync(video, targetPath, container, ffmpeg, cancellationToken).ConfigureAwait(false);
        }

        return await AssembleVideoOnlyAsync(video, targetPath, container, ffmpeg, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> AssembleVideoOnlyAsync(Track video, string targetPath, string container, string? ffmpeg, CancellationToken cancellationToken)
    {
        if (!video.IsTransportStream)
        {
            // fMP4 / WebM pieces already form a playable file: join straight into the target (with the track's own
            // extension if the target was meant for another container, for example after a failed merge).
            var ownTarget = targetPath.EndsWith(video.Extension, StringComparison.OrdinalIgnoreCase) ? targetPath : Path.ChangeExtension(targetPath, video.Extension);
            var partial = FileNamer.PartialPathFor(ownTarget);
            await JoinAsync(video, partial, cancellationToken).ConfigureAwait(false);
            return MoveIntoPlace(partial, ownTarget);
        }

        if (targetPath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
        {
            var partial = FileNamer.PartialPathFor(targetPath);
            await JoinAsync(video, partial, cancellationToken).ConfigureAwait(false);
            return MoveIntoPlace(partial, targetPath);
        }

        var joined = Path.Combine(video.Folder, "joined.ts");
        if (!File.Exists(joined))
        {
            await JoinAsync(video, joined, cancellationToken).ConfigureAwait(false);
        }

        if (ffmpeg is not null)
        {
            var output = FileNamer.PartialPathFor(targetPath);
            var result = await FfmpegRunner.RunAsync(ffmpeg, FfmpegRunner.RemuxArguments(joined, output, container), video.Media.TotalDurationSeconds, null, cancellationToken).ConfigureAwait(false);
            if (result.Succeeded && File.Exists(output))
            {
                return MoveIntoPlace(output, targetPath);
            }

            DeleteQuietly(output);
        }

        // No FFmpeg (or it failed): keep the stream as it is, a .ts file next to where the .mp4 would have gone.
        var tsPath = FileNamer.UniquePath(Path.GetDirectoryName(targetPath)!, Path.GetFileNameWithoutExtension(targetPath) + ".ts", File.Exists);
        File.Move(joined, tsPath);
        return tsPath;
    }

    /// <summary>Joins a track's pieces in order, each init section before the pieces that use it.</summary>
    private static async Task JoinAsync(Track track, string output, CancellationToken cancellationToken)
    {
        var maps = DistinctMaps(track.Media);
        await using var stream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, HttpFileDownloader.BufferSize, useAsync: true);
        HlsMap? current = null;
        for (var i = 0; i < track.Media.Segments.Count; i++)
        {
            var segment = track.Media.Segments[i];
            if (segment.Map is { } map && map != current)
            {
                await AppendAsync(stream, MapPath(track.Folder, maps.IndexOf(map)), cancellationToken).ConfigureAwait(false);
                current = map;
            }

            await AppendAsync(stream, SegmentPath(track.Folder, i), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task AppendAsync(Stream output, string file, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, HttpFileDownloader.BufferSize, useAsync: true);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Renames the finished file to its name; never overwrites a file that appeared meanwhile.</summary>
    private static string MoveIntoPlace(string source, string target)
    {
        var destination = File.Exists(target)
            ? FileNamer.UniquePath(Path.GetDirectoryName(target)!, Path.GetFileName(target), File.Exists)
            : target;
        File.Move(source, destination);
        return destination;
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leave it; the temp cleanup handles it.
        }
    }

    // ---------------------------------------------------------------- HTTP

    private async Task<byte[]> GetKeyAsync(HlsKey key, DownloadRequest request, Dictionary<Uri, byte[]> cache, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        if (key.Uri is null)
        {
            throw new StreamFatalException("The stream's encryption key has no address.");
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!cache.TryGetValue(key.Uri, out var bytes))
            {
                bytes = await FetchWithRetriesAsync(key.Uri, null, request, cancellationToken).ConfigureAwait(false);
                if (bytes.Length != HlsDecryptor.KeyLength)
                {
                    throw new StreamFatalException("The stream's key is invalid (the session may have expired). Play the video again, then Retry.");
                }

                cache[key.Uri] = bytes;
            }

            return bytes;
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<string> GetTextAsync(Uri url, IReadOnlyDictionary<string, string>? headers, Uri cookieOwner, CancellationToken cancellationToken)
    {
        var bytes = await FetchWithRetriesAsync(url, null, new DownloadRequest(cookieOwner, string.Empty, headers), cancellationToken, maxBytes: HlsParser.MaxLength).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// The size of a resource and whether its server takes byte ranges, from a one-byte ranged GET (<c>Content-Range</c>
    /// total on 206). A server that ignores ranges answers 200 with its <c>Content-Length</c>: size known, no ranges.
    /// </summary>
    internal async Task<(long? Length, bool AcceptsRanges)> ProbeAsync(Uri url, DownloadRequest request, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (name, value) in HeadersFor(url, request))
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }

        message.Headers.TryAddWithoutValidation("Range", "bytes=0-0");
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (status is 401 or 403 or 410)
        {
            throw new StreamFatalException($"The link has expired or access was refused (HTTP {status}). Play the video again, then Retry.");
        }

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            return (response.Content.Headers.ContentRange?.Length, response.Content.Headers.ContentRange?.Length is not null);
        }

        return response.IsSuccessStatusCode ? (response.Content.Headers.ContentLength, false) : throw new StreamFatalException($"The server refused the video (HTTP {status}).");
    }

    /// <summary>One GET with retries for transient problems; 401/403/410 mean an expired session (no retry).</summary>
    private async Task<byte[]> FetchWithRetriesAsync(Uri url, HlsByteRange? range, DownloadRequest request, CancellationToken cancellationToken, long maxBytes = 512L * 1024 * 1024)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (attempt > 0)
            {
                await _delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), cancellationToken).ConfigureAwait(false);
            }

            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Get, url);
                foreach (var (name, value) in HeadersFor(url, request))
                {
                    message.Headers.TryAddWithoutValidation(name, value);
                }

                if (range is not null)
                {
                    message.Headers.TryAddWithoutValidation("Range", range.ToRangeHeader());
                }

                using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (status is 401 or 403 or 410)
                {
                    throw new StreamFatalException($"The link has expired or access was refused (HTTP {status}). Play the video again, then Retry.");
                }

                if (status is 408 or 429 || status >= 500)
                {
                    if (attempt < MaxRetries)
                    {
                        continue;
                    }

                    throw new StreamFatalException($"The server is busy or failing (HTTP {status}).");
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new StreamFatalException($"The server refused a piece of the stream (HTTP {status}).");
                }

                if (response.Content.Headers.ContentLength > maxBytes)
                {
                    throw new StreamFatalException("A piece of the stream is unexpectedly large.");
                }

                var data = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

                // A server that ignored the Range header sent the whole resource: cut out our slice.
                if (range is not null && response.StatusCode == HttpStatusCode.OK && data.LongLength > range.Length)
                {
                    if (range.Offset + range.Length > data.LongLength)
                    {
                        throw new StreamFatalException("A piece of the stream is shorter than the playlist says.");
                    }

                    data = data.AsSpan((int)range.Offset, (int)range.Length).ToArray();
                }

                return data;
            }
            catch (HttpRequestException) when (attempt < MaxRetries && !cancellationToken.IsCancellationRequested)
            {
                // Network trouble: try again.
            }
            catch (TaskCanceledException) when (attempt < MaxRetries && !cancellationToken.IsCancellationRequested)
            {
                // HttpClient timeout: try again.
            }
            catch (IOException) when (attempt < MaxRetries && !cancellationToken.IsCancellationRequested)
            {
                // Connection dropped mid-body: try again.
            }
        }
    }

    /// <summary>The request's headers, without Cookie for any host other than the one the cookies were fetched for.</summary>
    internal static IEnumerable<KeyValuePair<string, string>> HeadersFor(Uri target, DownloadRequest request)
    {
        if (request.Headers is null)
        {
            yield break;
        }

        var sameHost = string.Equals(target.Host, request.Url.Host, StringComparison.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            if (sameHost || !header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
            {
                yield return header;
            }
        }
    }

}

/// <summary>A stream download problem that retrying won't fix; the message is shown to the user (never contains a URL).</summary>
internal sealed class StreamFatalException(string message) : Exception(message);

/// <summary>
/// One track handed to the piece engine: its pieces (as an HLS media playlist; DASH tracks are converted, Phase 7b) and
/// the file type its joined pieces form: ".ts" (MPEG-TS, needs FFmpeg to become MP4), ".mp4" or ".webm" (playable as they are).
/// </summary>
internal sealed record StreamTrack(HlsMediaPlaylist Media, string Extension)
{
    public bool IsTransportStream => Extension == ".ts";

    public static StreamTrack FromHls(HlsMediaPlaylist media) => new(media, media.IsFragmentedMp4 ? ".mp4" : ".ts");
}
