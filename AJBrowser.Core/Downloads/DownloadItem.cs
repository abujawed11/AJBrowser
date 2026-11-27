using AJBrowser.Core.Security;

namespace AJBrowser.Core.Downloads;

/// <summary>Who does the downloading (plan §6.1).</summary>
public enum DownloadKind
{
    /// <summary>The WebView2 runtime (a page link or file); AJBrowser only watches it.</summary>
    Browser,

    /// <summary>AJBrowser's own <see cref="HttpFileDownloader"/> (detected media from Phase 5 on).</summary>
    App,
}

/// <summary>What an app download fetches: one file, or the pieces of an HLS stream (Phase 6).</summary>
public enum DownloadSource
{
    File,
    Hls,

    /// <summary>A DASH stream (Phase 7b): <see cref="DownloadItem.SourceUrl"/> is the manifest, the tracks are chosen by id.</summary>
    Dash,

    /// <summary>
    /// A video fetched by yt-dlp (Phase 7.5b): <see cref="DownloadItem.SourceUrl"/> is the web page,
    /// <see cref="DownloadItem.VideoTrackId"/>/<see cref="DownloadItem.AudioTrackId"/> the yt-dlp format ids.
    /// </summary>
    YtDlp,
}

public enum DownloadState
{
    Queued,
    Downloading,
    Paused,
    Completed,
    Failed,
    Cancelled,

    /// <summary>Stopped by something outside the user's control (app closed, browser process gone).</summary>
    Interrupted,
}

/// <summary>What a server said about resuming: needed to continue an app download with an HTTP Range request.</summary>
public sealed record ResumeInfo(bool AcceptsRanges, string? ETag, string? LastModified)
{
    /// <summary>Resuming is safe only if the server takes ranges and we can check the file hasn't changed.</summary>
    public bool CanResume => AcceptsRanges && (ETag is not null || LastModified is not null);
}

/// <summary>
/// One download, as an immutable snapshot (change it with <c>with { … }</c>). Persisted fields are saved in the
/// <c>downloads</c> table; <see cref="BytesPerSecond"/> and <see cref="Remaining"/> exist only while it runs.
/// </summary>
public sealed record DownloadItem
{
    public long Id { get; init; }
    public DownloadKind Kind { get; init; }

    /// <summary>App downloads: a single file, or an HLS stream (<see cref="SourceUrl"/> is then its media playlist).</summary>
    public DownloadSource Source { get; init; }

    /// <summary>HLS: the separate sound track's media playlist, merged into the result (Phase 6c). Never logged.</summary>
    public string? AudioSourceUrl { get; init; }

    /// <summary>DASH: the chosen picture track's <c>Representation@id</c> (Phase 7b).</summary>
    public string? VideoTrackId { get; init; }

    /// <summary>DASH: the separate sound track's <c>Representation@id</c>, merged with FFmpeg; null = picture only or muxed.</summary>
    public string? AudioTrackId { get; init; }
    public DownloadState State { get; init; }

    /// <summary>The real address (needed for retry). Never log it: use <see cref="DisplayUrl"/>.</summary>
    public required string SourceUrl { get; init; }

    public string? PageUrl { get; init; }
    public required string FilePath { get; init; }
    public long? TotalBytes { get; init; }
    public long ReceivedBytes { get; init; }
    public DateTime StartedAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; init; }

    /// <summary>A short, user-readable reason when <see cref="State"/> is Failed or Interrupted (never contains URLs).</summary>
    public string? Error { get; init; }

    /// <summary>App downloads: what the server said about resuming.</summary>
    public ResumeInfo? Resume { get; init; }

    /// <summary>
    /// Browser downloads: the runtime says this one can be resumed. WebView2 only reports this once a download has
    /// stopped (paused or interrupted); while it runs it's always false, so it must not decide whether Pause is offered.
    /// </summary>
    public bool BrowserCanResume { get; init; }

    /// <summary>Current speed (smoothed), while downloading.</summary>
    public double? BytesPerSecond { get; init; }

    /// <summary>Estimated time left, when the total size and the speed are known.</summary>
    public TimeSpan? Remaining { get; init; }

    public string FileName => Path.GetFileName(FilePath);

    /// <summary>
    /// The address without query, fragment or user info (signed links carry secrets there). App downloads (detected
    /// media) also hide tokens in the path (<see cref="Redactor.MediaUrl(string?)"/>, spike F12).
    /// </summary>
    public string DisplayUrl => Kind == DownloadKind.App ? Redactor.MediaUrl(SourceUrl) : Redactor.Url(SourceUrl);

    /// <summary>0–1, or null when the total size is unknown (show an indeterminate bar).</summary>
    public double? Fraction => TotalBytes is > 0 ? Math.Clamp((double)ReceivedBytes / TotalBytes.Value, 0, 1) : null;

    public bool IsActive => State is DownloadState.Queued or DownloadState.Downloading;

    public bool IsFinished => State is DownloadState.Completed or DownloadState.Failed or DownloadState.Cancelled or DownloadState.Interrupted;

    /// <summary>
    /// Any running download can be paused, like in Chrome/Edge: Pause works on a running browser download (spike F5),
    /// and whether it can continue is only known afterwards (<see cref="CanResumeNow"/>).
    /// </summary>
    public bool CanPause => State == DownloadState.Downloading;

    /// <summary>
    /// A download the user paused can always continue (WebView2's Resume() works on user-paused downloads; a server
    /// without range support makes it start over). One stopped by an error needs the runtime's CanResume.
    /// </summary>
    public bool CanResumeNow => State == DownloadState.Paused
                                || (State == DownloadState.Interrupted && (Kind == DownloadKind.App || BrowserCanResume));

    public bool CanCancel => State is DownloadState.Queued or DownloadState.Downloading or DownloadState.Paused;

    public bool CanRetry => State is DownloadState.Failed or DownloadState.Cancelled or DownloadState.Interrupted;
}

/// <summary>Which state changes are allowed. Anything else is a bug and is refused.</summary>
public static class DownloadStateMachine
{
    public static bool CanMove(DownloadState from, DownloadState to) => from == to || (from, to) switch
    {
        (DownloadState.Queued, DownloadState.Downloading or DownloadState.Paused or DownloadState.Cancelled or DownloadState.Failed or DownloadState.Interrupted) => true,
        (DownloadState.Downloading, DownloadState.Paused or DownloadState.Completed or DownloadState.Failed or DownloadState.Cancelled or DownloadState.Interrupted) => true,
        (DownloadState.Paused, DownloadState.Downloading or DownloadState.Queued or DownloadState.Cancelled or DownloadState.Interrupted or DownloadState.Failed) => true,
        (DownloadState.Failed or DownloadState.Cancelled or DownloadState.Interrupted, DownloadState.Queued) => true,
        (DownloadState.Interrupted, DownloadState.Downloading) => true, // a browser download resumed by the runtime
        _ => false,
    };
}
