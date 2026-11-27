using AJBrowser.Core.Downloads;
using AJBrowser.Core.Media.Hls;

namespace AJBrowser.Core.Media.Dash;

/// <summary>What a DASH manifest offers, for the picker (Phase 7c).</summary>
/// <param name="Video">Picture tracks, best first.</param>
/// <param name="Audio">Separate sound tracks, in manifest order.</param>
/// <param name="ReferencedFiles">Whole-file tracks' addresses, so the page's own ranged requests for them don't show up as extra rows.</param>
/// <param name="Manifest">The parsed manifest (for <see cref="DashAnalysis.PickAudio"/> per quality), null when it couldn't be read.</param>
public sealed record DashInspection(
    DashSupport Support,
    string? Problem,
    IReadOnlyList<DashRepresentation> Video,
    IReadOnlyList<DashRepresentation> Audio,
    double? DurationSeconds,
    IReadOnlyList<Uri> ReferencedFiles,
    DashManifest? Manifest = null)
{
    /// <summary>The picture comes without sound, which is a separate track (merged with FFmpeg).</summary>
    public bool HasSeparateAudio => Audio.Count > 0 && Video.Any(v => v.Kind == DashTrackKind.Video);
}

/// <summary>What <see cref="DashDownloader.PrepareAsync"/> decided before a DASH download is queued.</summary>
/// <param name="Audio">The sound track to merge in (only when FFmpeg is available), else null.</param>
/// <param name="HasSeparateAudio">The picture has no sound of its own (with <see cref="Audio"/> null: it will be silent).</param>
public sealed record DashPlan(
    DashSupport Support,
    string? Problem,
    DashRepresentation? Video,
    DashRepresentation? Audio,
    string OutputExtension,
    bool HasSeparateAudio,
    double? DurationSeconds)
{
    public bool CanDownload => Support == DashSupport.Supported && Video is not null;

    /// <summary>(Picture + sound bitrate) × length, when both are known (always an estimate).</summary>
    public long? EstimatedBytes => Video is { } v && DurationSeconds is { } d ? HlsAnalysis.EstimateBytes(v.Bandwidth + (Audio?.Bandwidth ?? 0), d) : null;
}

/// <summary>
/// Downloads an unprotected, static DASH video (plan Phase 7b) with the piece engine of <see cref="HlsDownloader"/>:
/// each chosen track becomes a list of pieces (init section + segments, or 8 MB byte ranges of a whole-file track),
/// fetched in parallel with retries into the job's temp folder (resumable), joined, and — picture and sound being
/// separate in DASH — merged with FFmpeg. Without FFmpeg only the picture track is saved (or a track that already has
/// sound). The manifest is fetched again on every run, so fresh piece addresses are used after a pause or restart.
/// </summary>
public sealed class DashDownloader : IStreamDownloader
{
    /// <summary>Whole-file tracks are fetched in slices of this size (parallel, resumable, bounded memory).</summary>
    public const long ChunkSize = 8L * 1024 * 1024;

    /// <summary>A whole file from a server without range support is fetched in one go, up to this size.</summary>
    public const long MaxUnrangedFile = 512L * 1024 * 1024;

    private readonly HlsDownloader _engine;
    private readonly Func<string?> _ffmpegPath;

    /// <param name="engine">The piece engine (its temp folder is shared: one folder per job id).</param>
    /// <param name="ffmpegPath">The FFmpeg to use right now, or null.</param>
    public DashDownloader(HlsDownloader engine, Func<string?> ffmpegPath)
    {
        _engine = engine;
        _ffmpegPath = ffmpegPath;
    }

    public void DeleteTemp(long jobId) => _engine.DeleteTemp(jobId);

    // ---------------------------------------------------------------- before queueing

    /// <summary>Reads the manifest for the picker. Never throws for network or format problems (they come back as a problem text).</summary>
    public async Task<DashInspection> InspectAsync(Uri manifestUrl, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken = default)
    {
        try
        {
            var manifest = await ReadManifestAsync(manifestUrl, headers, cancellationToken).ConfigureAwait(false);
            var support = DashAnalysis.Classify(manifest);
            var referenced = manifest.Representations.Where(r => r.IsSingleFile).Select(r => r.Segments[0].Uri).Distinct().ToList();
            return support == DashSupport.Supported
                ? new DashInspection(support, null, DashAnalysis.SortVideoBestFirst(manifest), manifest.Audio.Where(a => a.Segments.Count > 0).ToList(), DashAnalysis.Duration(manifest), referenced, manifest)
                : new DashInspection(support, DashAnalysis.Explain(support), [], [], DashAnalysis.Duration(manifest), referenced, manifest);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return new DashInspection(DashSupport.Empty, ProblemText(ex), [], [], null, []);
        }
    }

    /// <summary>
    /// Picks the picture track (<paramref name="videoId"/>, else the best) and the sound track (<paramref name="audioId"/>,
    /// else <see cref="DashAnalysis.PickAudio"/> for <paramref name="preferredLanguage"/>), and the output file type.
    /// The sound is only planned when FFmpeg is available to merge it. Never throws for network or format problems.
    /// </summary>
    public async Task<DashPlan> PrepareAsync(
        Uri manifestUrl,
        IReadOnlyDictionary<string, string>? headers,
        string? videoId = null,
        string? audioId = null,
        string? preferredLanguage = null,
        CancellationToken cancellationToken = default)
    {
        DashManifest manifest;
        try
        {
            manifest = await ReadManifestAsync(manifestUrl, headers, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return new DashPlan(DashSupport.Empty, ProblemText(ex), null, null, ".mp4", false, null);
        }

        var support = DashAnalysis.Classify(manifest);
        var duration = DashAnalysis.Duration(manifest);
        if (support != DashSupport.Supported)
        {
            return new DashPlan(support, DashAnalysis.Explain(support), null, null, ".mp4", false, duration);
        }

        var videos = DashAnalysis.SortVideoBestFirst(manifest);
        var video = videos.FirstOrDefault(v => videoId is not null && v.Id == videoId) ?? videos[0];
        var audio = video.Kind == DashTrackKind.Muxed
            ? null
            : manifest.Audio.FirstOrDefault(a => audioId is not null && a.Id == audioId && a.Segments.Count > 0)
              ?? DashAnalysis.PickAudio(manifest, video, preferredLanguage);
        var separate = video.Kind == DashTrackKind.Video && audio is not null;
        var merged = separate && _ffmpegPath() is not null ? audio : null;

        var extension = merged is not null
            ? DashAnalysis.SelectContainer(video, merged).Container switch { "WebM" => ".webm", "MKV" => ".mkv", _ => ".mp4" }
            : TrackExtension(video);
        return new DashPlan(support, null, video, merged, extension, separate, duration);
    }

    // ---------------------------------------------------------------- the download

    /// <summary>
    /// Downloads <see cref="DownloadRequest.VideoTrackId"/> (and <see cref="DownloadRequest.AudioTrackId"/>, merged)
    /// of the manifest at <see cref="DownloadRequest.Url"/> into <see cref="DownloadRequest.TargetPath"/>.
    /// </summary>
    public Task<DownloadResult> DownloadAsync(long jobId, DownloadRequest request, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken) =>
        _engine.DownloadTracksAsync(jobId, request, async token =>
        {
            var manifest = await ReadManifestAsync(request.Url, request.Headers, token).ConfigureAwait(false);
            if (DashAnalysis.Explain(DashAnalysis.Classify(manifest)) is { } problem)
            {
                throw new StreamFatalException(problem);
            }

            var video = request.VideoTrackId is { } videoId
                ? manifest.Video.FirstOrDefault(v => v.Id == videoId)
                : DashAnalysis.SortVideoBestFirst(manifest).FirstOrDefault();
            var tracks = new List<StreamTrack> { await ToTrackAsync(video ?? throw Changed(), request, token).ConfigureAwait(false) };
            if (request.AudioTrackId is { } audioId)
            {
                var audio = manifest.Audio.FirstOrDefault(a => a.Id == audioId) ?? throw Changed();
                tracks.Add(await ToTrackAsync(audio, request, token).ConfigureAwait(false));
            }

            return tracks;
        }, progress, cancellationToken);

    private static StreamFatalException Changed() =>
        new("The video's stream changed since the download started. Start the download again from the page.");

    /// <summary>A representation as a piece list for the engine; a whole file becomes 8 MB byte ranges when the server allows.</summary>
    private async Task<StreamTrack> ToTrackAsync(DashRepresentation track, DownloadRequest request, CancellationToken cancellationToken)
    {
        var extension = TrackExtension(track);
        var segments = new List<HlsSegment>();
        if (track.IsSingleFile)
        {
            var file = track.Segments[0];
            var (length, ranges) = await _engine.ProbeAsync(file.Uri, request, cancellationToken).ConfigureAwait(false);
            if (ranges && length is > 0)
            {
                var count = (int)((length.Value + ChunkSize - 1) / ChunkSize);
                for (var i = 0; i < count; i++)
                {
                    var offset = i * ChunkSize;
                    segments.Add(new HlsSegment(file.Uri, file.DurationSeconds / count, i, new HlsByteRange(Math.Min(ChunkSize, length.Value - offset), offset), null, null, false));
                }
            }
            else if (length > MaxUnrangedFile)
            {
                throw new StreamFatalException("This video file is too large to fetch from a server that doesn't support partial downloads.");
            }
            else
            {
                segments.Add(new HlsSegment(file.Uri, file.DurationSeconds, 0, null, null, null, false));
            }
        }
        else
        {
            var map = track.Initialization is { } init ? new HlsMap(init.Uri, init.ByteRange) : null;
            for (var i = 0; i < track.Segments.Count; i++)
            {
                var piece = track.Segments[i];
                segments.Add(new HlsSegment(piece.Uri, piece.DurationSeconds, i, piece.ByteRange, null, map, false));
            }
        }

        var target = segments.Count == 0 ? 0 : segments.Max(s => s.DurationSeconds);
        return new StreamTrack(new HlsMediaPlaylist(request.Url, target, 0, true, "VOD", segments), extension);
    }

    /// <summary>What a track's joined pieces form: WebM, MPEG-TS or (by default) MP4.</summary>
    internal static string TrackExtension(DashRepresentation track)
    {
        var mime = track.MimeType?.ToLowerInvariant() ?? string.Empty;
        return mime.EndsWith("/webm", StringComparison.Ordinal) ? ".webm"
            : mime.EndsWith("/mp2t", StringComparison.Ordinal) ? ".ts"
            : ".mp4";
    }

    // ---------------------------------------------------------------- helpers

    private async Task<DashManifest> ReadManifestAsync(Uri manifestUrl, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
    {
        var text = await _engine.GetTextAsync(manifestUrl, headers, manifestUrl, cancellationToken).ConfigureAwait(false);
        return DashParser.TryParse(text, manifestUrl) ?? throw new StreamFatalException("The stream's manifest couldn't be read.");
    }

    private static bool IsExpected(Exception ex) => ex is StreamFatalException or HttpRequestException or TaskCanceledException or IOException;

    private static string ProblemText(Exception ex) => ex is StreamFatalException ? ex.Message : "The stream's manifest couldn't be read.";
}
