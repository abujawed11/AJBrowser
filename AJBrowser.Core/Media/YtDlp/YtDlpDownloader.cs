using System.Globalization;
using AJBrowser.Core.Downloads;
using AJBrowser.Core.Tools;

namespace AJBrowser.Core.Media.YtDlp;

/// <summary>The programs a yt-dlp run uses, asked for at the moment they're needed (Settings can change in between).</summary>
public sealed record YtDlpTools(Func<string?> YtDlp, Func<string?> Ffmpeg, Func<string?> Deno);

/// <summary>The browser session handed to yt-dlp: the cookies the browser would send to those addresses, and its user agent.</summary>
public sealed record BrowserSession(IReadOnlyList<BrowserCookie> Cookies, string? UserAgent)
{
    public static readonly BrowserSession Empty = new([], null);
}

/// <summary>What <see cref="YtDlpDownloader.InspectAsync"/> found: the video's information, or why there is none.</summary>
public sealed record YtDlpInspection(YtDlpInfo? Info, YtDlpProblem? Problem);

/// <summary>
/// Runs yt-dlp for the big platforms (plan Phase 7.5b). <see cref="InspectAsync"/> asks what a page offers
/// (<c>-J</c>, cached a few minutes per address); <see cref="DownloadAsync"/> runs a download job from the download
/// manager (<see cref="DownloadSource.YtDlp"/>): <see cref="DownloadRequest.Url"/> is the page,
/// <see cref="DownloadRequest.VideoTrackId"/>/<see cref="DownloadRequest.AudioTrackId"/> the chosen yt-dlp format ids
/// (either may be missing), and <see cref="DownloadRequest.TargetPath"/>'s extension the file type of a merge.
/// <list type="bullet">
/// <item>Each run gets the browser's current cookies for that site (<see cref="YtDlpCookieScope"/>) in a cookie file in
/// the job's temp folder, deleted when the run ends, and the browser's user agent.</item>
/// <item>Pause = stop the process (yt-dlp keeps its <c>.part</c> files in the job's temp folder); resume = run the same
/// command again (yt-dlp continues them). Cancel = <see cref="DeleteTemp"/>.</item>
/// <item>yt-dlp and what it starts (FFmpeg, Deno) end with AJBrowser (<see cref="ChildProcessJob"/>).</item>
/// </list>
/// </summary>
public sealed class YtDlpDownloader : IStreamDownloader
{
    /// <summary>How long a page's information is reused (the picker reopening, then Download).</summary>
    public static readonly TimeSpan InspectionCacheTime = TimeSpan.FromMinutes(5);

    /// <summary>yt-dlp usually answers in 2–6 s; YouTube with the JavaScript challenge can take longer.</summary>
    public static readonly TimeSpan InspectTimeout = TimeSpan.FromSeconds(90);

    public delegate Task<ProcessResult> Runner(string exe, IReadOnlyList<string> arguments, Action<string>? onOutput, bool keepOutput, CancellationToken cancellationToken);

    private readonly string _tempRoot;
    private readonly YtDlpTools _tools;
    private readonly Func<IReadOnlyList<Uri>, Task<BrowserSession>> _session;
    private readonly Runner _run;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, (DateTimeOffset At, YtDlpInfo Info)> _cache = [];
    private readonly object _cacheLock = new();

    /// <param name="tempRoot"><c>%LOCALAPPDATA%\AJBrowser\Temp\ytdlp</c>: one folder per job (and one per inspection).</param>
    /// <param name="session">The browser's cookies for some addresses + its user agent (the app reads the WebView2 profile).</param>
    public YtDlpDownloader(string tempRoot, YtDlpTools tools, Func<IReadOnlyList<Uri>, Task<BrowserSession>> session, Runner? run = null, Func<DateTimeOffset>? now = null)
    {
        _tempRoot = tempRoot;
        _tools = tools;
        _session = session;
        _run = run ?? ((exe, args, onOutput, keep, token) => ProcessRunner.RunAsync(exe, args, onOutput, keep, token));
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public string TempFolderFor(long jobId) => Path.Combine(_tempRoot, jobId.ToString(CultureInfo.InvariantCulture));

    public void DeleteTemp(long jobId) => DeleteFolder(TempFolderFor(jobId));

    // ---------------------------------------------------------------- inspect

    /// <summary>
    /// What yt-dlp can get from a page: title, length and formats, or a problem with a suggested fix. Never throws for
    /// tool, network or format problems. A cached answer is reused for <see cref="InspectionCacheTime"/>.
    /// </summary>
    public async Task<YtDlpInspection> InspectAsync(Uri pageUrl, CancellationToken cancellationToken = default)
    {
        var key = pageUrl.AbsoluteUri;
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var hit) && _now() - hit.At < InspectionCacheTime)
            {
                return new YtDlpInspection(hit.Info, null);
            }
        }

        if (_tools.YtDlp() is not { } ytdlp)
        {
            return new YtDlpInspection(null, NotInstalled);
        }

        var folder = Path.Combine(_tempRoot, "inspect-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = await SafeSessionAsync(pageUrl).ConfigureAwait(false);
            var cookieFile = session.Cookies.Count > 0 ? NetscapeCookieFile.Write(folder, session.Cookies) : null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(InspectTimeout);
            ProcessResult result;
            try
            {
                result = await _run(ytdlp, YtDlpArguments.Inspect(pageUrl, _tools.Deno(), cookieFile, session.UserAgent), null, true, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new YtDlpInspection(null, new YtDlpProblem("yt-dlp took too long to read this page. Try again.", YtDlpFix.Retry));
            }

            if (!result.Succeeded)
            {
                return new YtDlpInspection(null, result.ExitCode == -1 ? CouldNotStart : YtDlpErrors.Explain(result.ErrorTail));
            }

            YtDlpInfo info;
            try
            {
                info = YtDlpInfoParser.ParseOutput(result.Output);
            }
            catch (FormatException)
            {
                return new YtDlpInspection(null, new YtDlpProblem("yt-dlp's answer couldn't be read. Update yt-dlp, then try again.", YtDlpFix.UpdateYtDlp));
            }

            lock (_cacheLock)
            {
                if (_cache.Count >= 20)
                {
                    _cache.Remove(_cache.MinBy(e => e.Value.At).Key);
                }

                _cache[key] = (_now(), info);
            }

            return new YtDlpInspection(info, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new YtDlpInspection(null, new YtDlpProblem($"Couldn't prepare yt-dlp: {ex.Message}", YtDlpFix.Retry));
        }
        finally
        {
            DeleteFolder(folder); // includes the cookie file
        }
    }

    /// <summary>Forgets a page's cached information (after a failed download, so Retry asks again).</summary>
    public void Forget(Uri pageUrl)
    {
        lock (_cacheLock)
        {
            _cache.Remove(pageUrl.AbsoluteUri);
        }
    }

    // ---------------------------------------------------------------- download

    /// <summary>The <c>-f</c> value of a job: "137+140", "18" or "140".</summary>
    public static string FormatSelector(DownloadRequest request) =>
        string.Join("+", new[] { request.VideoTrackId, request.AudioTrackId }.Where(id => !string.IsNullOrWhiteSpace(id)));

    public async Task<DownloadResult> DownloadAsync(long jobId, DownloadRequest request, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        if (_tools.YtDlp() is not { } ytdlp)
        {
            return Failed(NotInstalled.Message);
        }

        var selector = FormatSelector(request);
        var merges = request.VideoTrackId is not null && request.AudioTrackId is not null;
        var ffmpeg = _tools.Ffmpeg();
        if (selector.Length == 0)
        {
            return Failed("The download doesn't say which quality to get. Start it again from the page.");
        }

        if (merges && ffmpeg is null)
        {
            return Failed("Joining picture and sound needs FFmpeg. Install it in Settings → Downloads, then Retry.");
        }

        var folder = TempFolderFor(jobId);
        var tracker = new YtDlpProgressTracker(selector);
        try
        {
            Directory.CreateDirectory(folder);
            var session = await SafeSessionAsync(request.Url).ConfigureAwait(false);
            var cookieFile = session.Cookies.Count > 0 ? NetscapeCookieFile.Write(folder, session.Cookies) : null;
            var options = new YtDlpDownloadOptions(
                request.Url,
                selector,
                merges ? MergeFormatFor(request.TargetPath) : null,
                Path.GetDirectoryName(Path.GetFullPath(request.TargetPath))!,
                Path.GetFileNameWithoutExtension(request.TargetPath),
                folder,
                ffmpeg,
                _tools.Deno(),
                cookieFile,
                session.UserAgent);

            ProcessResult result;
            try
            {
                result = await _run(ytdlp, YtDlpArguments.Download(options), line =>
                {
                    bool changed;
                    long received;
                    long? total;
                    lock (tracker)
                    {
                        changed = tracker.Read(line);
                        received = tracker.Received;
                        total = tracker.Total;
                    }

                    if (changed)
                    {
                        progress?.Report(new DownloadProgress(received, total));
                    }
                }, false, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                NetscapeCookieFile.Delete(folder);
            }

            if (result.ExitCode == -1)
            {
                return Failed(CouldNotStart.Message);
            }

            if (!result.Succeeded)
            {
                Forget(request.Url);
                return new DownloadResult(DownloadOutcome.Failed, tracker.Received, null, null, YtDlpErrors.Explain(result.ErrorTail).Message);
            }

            if (tracker.FinalPath is not { } finalPath || !File.Exists(finalPath))
            {
                return Failed("yt-dlp finished, but the file wasn't found. Retry.");
            }

            DeleteTemp(jobId);
            var size = new FileInfo(finalPath).Length;
            progress?.Report(new DownloadProgress(size, size));
            return new DownloadResult(DownloadOutcome.Completed, size, size, null, null,
                string.Equals(Path.GetFullPath(finalPath), Path.GetFullPath(request.TargetPath), StringComparison.OrdinalIgnoreCase) ? null : finalPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            NetscapeCookieFile.Delete(folder);
            return new DownloadResult(DownloadOutcome.Cancelled, tracker.Received, tracker.Total, null); // .part files kept for resuming
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NetscapeCookieFile.Delete(folder);
            return Failed($"Couldn't write the file: {ex.Message}");
        }

        static DownloadResult Failed(string message) => new(DownloadOutcome.Failed, 0, null, null, message);
    }

    /// <summary>The merge container from the target's extension (chosen when the row was built): mp4, webm or mkv.</summary>
    public static string MergeFormatFor(string targetPath) => Path.GetExtension(targetPath).ToLowerInvariant() switch
    {
        ".webm" => "webm",
        ".mkv" => "mkv",
        _ => "mp4",
    };

    private static readonly YtDlpProblem NotInstalled = new("yt-dlp isn't installed. Install it in Settings → Downloads.", YtDlpFix.UpdateYtDlp);

    private static readonly YtDlpProblem CouldNotStart = new("yt-dlp couldn't be started (your antivirus may have blocked it). Reinstall it in Settings → Downloads.", YtDlpFix.UpdateYtDlp);

    /// <summary>The session for a page; without one (no browser engine running yet) yt-dlp goes without cookies.</summary>
    private async Task<BrowserSession> SafeSessionAsync(Uri pageUrl)
    {
        try
        {
            return await _session(YtDlpCookieScope.UrlsFor(pageUrl)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException or System.Runtime.InteropServices.COMException)
        {
            return BrowserSession.Empty;
        }
    }

    private static void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In use (a process still closing): the startup cleanup removes it.
        }
    }
}
