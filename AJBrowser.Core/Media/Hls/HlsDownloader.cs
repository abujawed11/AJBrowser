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
    string? Problem)
{
    public bool CanDownload => Support == HlsSupport.Supported && MediaPlaylistUrl is not null && Media is not null;

    /// <summary>Bitrate × duration, when both are known (always an estimate).</summary>
    public long? EstimatedBytes => Variant is { } v && Media is { } m ? HlsAnalysis.EstimateBytes(v.EffectiveBandwidth, m.TotalDurationSeconds) : null;
}

/// <summary>
/// Downloads an unprotected, finished (VOD) HLS stream into one file (plan Phase 6):
/// <list type="bullet">
/// <item>Pieces (segments) are fetched <see cref="Parallelism"/> at a time with the page's allowlisted headers, each
/// retried on network trouble, AES-128 ones decrypted, byte-range slices honoured. Each finished piece is saved in a
/// temporary folder per job, so pausing or an app restart continues where it stopped.</item>
/// <item>The <b>Cookie</b> header (fetched for the playlist's address) is sent only to that same host: pieces often
/// come from other CDN hosts, which must not receive the site's cookies.</item>
/// <item>Assembly: fMP4 streams (init section + pieces) are already a valid MP4. TS streams become MP4 through FFmpeg
/// when it's available (a local file in, a local file out: FFmpeg never touches the network or cookies), otherwise
/// they're saved as .ts, which common players open.</item>
/// </list>
/// Separate audio tracks (EXT-X-MEDIA with their own playlist) aren't merged yet: the stream's main playlist is used.
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
    /// Fetches the playlist; for a master playlist picks a quality (<paramref name="variantUri"/>, else the best) and
    /// fetches its media playlist. Decides whether it can be downloaded and which file extension the result gets.
    /// </summary>
    public async Task<HlsPlan> PrepareAsync(Uri playlistUrl, IReadOnlyDictionary<string, string>? headers, Uri? variantUri = null, CancellationToken cancellationToken = default)
    {
        var playlist = HlsParser.TryParse(await GetTextAsync(playlistUrl, headers, playlistUrl, cancellationToken).ConfigureAwait(false), playlistUrl);
        HlsVariant? variant = null;
        var separateAudio = false;
        var mediaUrl = playlistUrl;

        if (playlist is HlsMasterPlaylist master)
        {
            if (HlsAnalysis.IsProtected(master))
            {
                return Plan(HlsSupport.Protected);
            }

            var sorted = HlsAnalysis.SortBestFirst(master.Variants);
            variant = sorted.FirstOrDefault(v => variantUri is not null && v.Uri == variantUri) ?? sorted.FirstOrDefault();
            if (variant is null)
            {
                return Plan(HlsSupport.Empty);
            }

            separateAudio = variant.AudioGroup is { } group
                            && master.Renditions.Any(r => r.Type == "AUDIO" && r.GroupId == group && r.Uri is not null);
            mediaUrl = variant.Uri;
            playlist = HlsParser.TryParse(await GetTextAsync(mediaUrl, headers, playlistUrl, cancellationToken).ConfigureAwait(false), mediaUrl);
        }

        if (playlist is not HlsMediaPlaylist media)
        {
            return Plan(HlsSupport.Empty) with { Problem = "The stream's playlist couldn't be read." };
        }

        var support = HlsAnalysis.Classify(media);
        var (container, _) = HlsAnalysis.SelectContainer(variant?.Codecs);
        var extension = media.IsFragmentedMp4 ? ".mp4"
            : _ffmpegPath() is not null ? container switch { "WebM" => ".webm", "MKV" => ".mkv", _ => ".mp4" }
            : ".ts";
        return new HlsPlan(support, mediaUrl, variant, media, extension, separateAudio, HlsAnalysis.Explain(support));

        static HlsPlan Plan(HlsSupport support) => new(support, null, null, null, ".mp4", false, HlsAnalysis.Explain(support));
    }

    // ---------------------------------------------------------------- the download

    /// <summary>
    /// Downloads the stream whose <b>media</b> playlist is <see cref="DownloadRequest.Url"/> into
    /// <see cref="DownloadRequest.TargetPath"/>. Never throws for network, HTTP or file problems (they come back as Failed).
    /// Cancellation keeps the finished pieces (pause); <see cref="DeleteTemp"/> removes them (cancel).
    /// </summary>
    public async Task<DownloadResult> DownloadAsync(long jobId, DownloadRequest request, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var folder = TempFolderFor(jobId);
        long[] sizes = [];
        try
        {
            var text = await GetTextAsync(request.Url, request.Headers, request.Url, cancellationToken).ConfigureAwait(false);
            if (HlsParser.TryParse(text, request.Url) is not HlsMediaPlaylist media)
            {
                return Failed("The stream's playlist couldn't be read.");
            }

            if (HlsAnalysis.Explain(HlsAnalysis.Classify(media)) is { } problem)
            {
                return Failed(problem);
            }

            PrepareFolder(folder, media);
            var total = media.Segments.Count;
            sizes = new long[total];
            var done = 0;
            for (var i = 0; i < total; i++)
            {
                if (File.Exists(SegmentPath(folder, i)))
                {
                    sizes[i] = new FileInfo(SegmentPath(folder, i)).Length;
                    done++;
                }
            }

            void Report()
            {
                var received = sizes.Sum();
                var finished = Volatile.Read(ref done);
                long? estimate = finished > 0 ? (long)((double)received / finished * total) : null;
                progress?.Report(new DownloadProgress(received, estimate));
            }

            Report();

            // Keys and init sections are small and shared by many pieces: fetch each once.
            var keys = new Dictionary<Uri, byte[]>();
            var keyLock = new SemaphoreSlim(1, 1);
            await EnsureMapsAsync(folder, media, request, cancellationToken).ConfigureAwait(false);

            var pending = Enumerable.Range(0, total).Where(i => !File.Exists(SegmentPath(folder, i))).ToList();
            await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Parallelism), CancellationToken = cancellationToken }, async (index, token) =>
            {
                var segment = media.Segments[index];
                var data = await FetchWithRetriesAsync(segment.Uri, segment.ByteRange, request, token).ConfigureAwait(false);
                if (segment.Key is { IsAes128: true } key)
                {
                    var keyBytes = await GetKeyAsync(key, request, keys, keyLock, token).ConfigureAwait(false);
                    data = HlsDecryptor.Decrypt(data, keyBytes, HlsDecryptor.IvFor(segment));
                }

                var final = SegmentPath(folder, index);
                await File.WriteAllBytesAsync(final + ".part", data, token).ConfigureAwait(false);
                File.Move(final + ".part", final, overwrite: true);
                sizes[index] = data.LongLength;
                Interlocked.Increment(ref done);
                Report();
            }).ConfigureAwait(false);

            var finalPath = await AssembleAsync(folder, media, request.TargetPath, cancellationToken).ConfigureAwait(false);
            DeleteTemp(jobId);
            var size = new FileInfo(finalPath).Length;
            progress?.Report(new DownloadProgress(size, size));
            return new DownloadResult(DownloadOutcome.Completed, size, size, null, null, finalPath == request.TargetPath ? null : finalPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new DownloadResult(DownloadOutcome.Cancelled, sizes.Sum(), null, null); // pieces kept for resuming
        }
        catch (HlsFatalException ex)
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

    private async Task EnsureMapsAsync(string folder, HlsMediaPlaylist media, DownloadRequest request, CancellationToken cancellationToken)
    {
        var maps = DistinctMaps(media);
        for (var i = 0; i < maps.Count; i++)
        {
            var path = MapPath(folder, i);
            if (!File.Exists(path))
            {
                var data = await FetchWithRetriesAsync(maps[i].Uri, maps[i].ByteRange, request, cancellationToken).ConfigureAwait(false);
                await File.WriteAllBytesAsync(path, data, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static List<HlsMap> DistinctMaps(HlsMediaPlaylist media) =>
        media.Segments.Select(s => s.Map).OfType<HlsMap>().Distinct().ToList();

    // ---------------------------------------------------------------- assembly

    /// <summary>Joins the pieces in order (each init section before the pieces that use it) and, if needed, remuxes.</summary>
    private async Task<string> AssembleAsync(string folder, HlsMediaPlaylist media, string targetPath, CancellationToken cancellationToken)
    {
        var wantsRemux = !media.IsFragmentedMp4 && !targetPath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase);
        var joined = wantsRemux ? Path.Combine(folder, "joined.ts") : FileNamer.PartialPathFor(targetPath);
        await JoinAsync(folder, media, joined, cancellationToken).ConfigureAwait(false);

        if (!wantsRemux)
        {
            return MoveIntoPlace(joined, targetPath);
        }

        var ffmpeg = _ffmpegPath();
        if (ffmpeg is not null)
        {
            var output = FileNamer.PartialPathFor(targetPath);
            var container = Path.GetExtension(targetPath).ToLowerInvariant() switch { ".webm" => "WebM", ".mkv" => "MKV", _ => "MP4" };
            var result = await FfmpegRunner.RunAsync(ffmpeg, FfmpegRunner.RemuxArguments(joined, output, container), media.TotalDurationSeconds, null, cancellationToken).ConfigureAwait(false);
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

    private static async Task JoinAsync(string folder, HlsMediaPlaylist media, string output, CancellationToken cancellationToken)
    {
        var maps = DistinctMaps(media);
        await using var stream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, HttpFileDownloader.BufferSize, useAsync: true);
        HlsMap? current = null;
        for (var i = 0; i < media.Segments.Count; i++)
        {
            var segment = media.Segments[i];
            if (segment.Map is { } map && map != current)
            {
                await AppendAsync(stream, MapPath(folder, maps.IndexOf(map)), cancellationToken).ConfigureAwait(false);
                current = map;
            }

            await AppendAsync(stream, SegmentPath(folder, i), cancellationToken).ConfigureAwait(false);
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
            throw new HlsFatalException("The stream's encryption key has no address.");
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!cache.TryGetValue(key.Uri, out var bytes))
            {
                bytes = await FetchWithRetriesAsync(key.Uri, null, request, cancellationToken).ConfigureAwait(false);
                if (bytes.Length != HlsDecryptor.KeyLength)
                {
                    throw new HlsFatalException("The stream's key is invalid (the session may have expired). Play the video again, then Retry.");
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

    private async Task<string> GetTextAsync(Uri url, IReadOnlyDictionary<string, string>? headers, Uri cookieOwner, CancellationToken cancellationToken)
    {
        var bytes = await FetchWithRetriesAsync(url, null, new DownloadRequest(cookieOwner, string.Empty, headers), cancellationToken, maxBytes: HlsParser.MaxLength).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
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
                    throw new HlsFatalException($"The link has expired or access was refused (HTTP {status}). Play the video again, then Retry.");
                }

                if (status is 408 or 429 || status >= 500)
                {
                    if (attempt < MaxRetries)
                    {
                        continue;
                    }

                    throw new HlsFatalException($"The server is busy or failing (HTTP {status}).");
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new HlsFatalException($"The server refused a piece of the stream (HTTP {status}).");
                }

                if (response.Content.Headers.ContentLength > maxBytes)
                {
                    throw new HlsFatalException("A piece of the stream is unexpectedly large.");
                }

                var data = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

                // A server that ignored the Range header sent the whole resource: cut out our slice.
                if (range is not null && response.StatusCode == HttpStatusCode.OK && data.LongLength > range.Length)
                {
                    if (range.Offset + range.Length > data.LongLength)
                    {
                        throw new HlsFatalException("A piece of the stream is shorter than the playlist says.");
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

    private sealed class HlsFatalException(string message) : Exception(message);
}
