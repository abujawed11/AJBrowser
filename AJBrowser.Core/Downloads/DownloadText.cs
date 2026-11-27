using System.Globalization;

namespace AJBrowser.Core.Downloads;

/// <summary>
/// The words and numbers the download list shows: sizes, speed, time left and one status line per download.
/// Pure functions, so the UI stays thin and the wording is unit-tested.
/// </summary>
public static class DownloadText
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>"0 B", "512 B", "1.5 KB", "12.3 MB", "1.07 GB" (1024-based, like File Explorer).</summary>
    public static string Size(long bytes, IFormatProvider? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (bytes < 1024)
        {
            return string.Format(culture, "{0} B", Math.Max(0, bytes));
        }

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // Three significant digits: 1.07 GB, 12.3 MB, 456 KB.
        var format = value >= 100 ? "{0:0} {1}" : value >= 10 ? "{0:0.#} {1}" : "{0:0.##} {1}";
        return string.Format(culture, format, value, Units[unit]);
    }

    /// <summary>"1.2 MB/s".</summary>
    public static string Speed(double bytesPerSecond, IFormatProvider? culture = null) =>
        Size((long)Math.Round(Math.Max(0, bytesPerSecond)), culture) + "/s";

    /// <summary>"5 s left", "3 min left", "1 h 5 min left", "2 days left".</summary>
    public static string Remaining(TimeSpan remaining, IFormatProvider? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var seconds = Math.Max(1, (long)Math.Ceiling(remaining.TotalSeconds));
        if (seconds < 60)
        {
            return string.Format(culture, "{0} s left", seconds);
        }

        var minutes = (long)Math.Ceiling(seconds / 60.0);
        if (minutes < 60)
        {
            return string.Format(culture, "{0} min left", minutes);
        }

        var hours = minutes / 60;
        if (hours < 48)
        {
            var rest = minutes % 60;
            return rest == 0 ? string.Format(culture, "{0} h left", hours) : string.Format(culture, "{0} h {1} min left", hours, rest);
        }

        return string.Format(culture, "{0} days left", hours / 24);
    }

    /// <summary>
    /// One line under the file name, for example "3.2 MB of 10 MB · 1.1 MB/s · 8 s left", "Paused · 3.2 MB of 10 MB",
    /// "10 MB", "Failed · Network error". <paramref name="fileMissing"/>: a completed file that is no longer on disk.
    /// </summary>
    public static string Status(DownloadItem item, bool fileMissing = false, IFormatProvider? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        return item.State switch
        {
            DownloadState.Queued => "Waiting…",
            // Nothing received yet: the downloader is still preparing (yt-dlp reads the page again first, a few seconds).
            DownloadState.Downloading when item.ReceivedBytes <= 0 => Join("Starting…", item.TotalBytes is > 0 and var expected ? Size(expected, culture) : null),
            DownloadState.Downloading => Join(Progress(item, culture),
                                              item.BytesPerSecond is > 0 and var speed ? Speed(speed, culture) : null,
                                              item.Remaining is { } left ? Remaining(left, culture) : null),
            DownloadState.Paused => Join("Paused", Progress(item, culture)),
            DownloadState.Completed => fileMissing ? "Moved or deleted" : Size(item.TotalBytes ?? item.ReceivedBytes, culture),
            DownloadState.Failed => Join("Failed", item.Error),
            DownloadState.Cancelled => "Cancelled",
            DownloadState.Interrupted => Join("Interrupted", item.Error),
            _ => item.State.ToString(),
        };
    }

    /// <summary>"3.2 MB of 10 MB", or just "3.2 MB" when the total size is unknown.</summary>
    public static string Progress(DownloadItem item, IFormatProvider? culture = null) =>
        item.TotalBytes is > 0 and var total
            ? $"{Size(item.ReceivedBytes, culture)} of {Size(total, culture)}"
            : Size(item.ReceivedBytes, culture);

    private static string Join(params string?[] parts) =>
        string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}

/// <summary>
/// Translates what the WebView2 runtime reports about a browser download into our states. Takes the WebView2 enum
/// <em>names</em> (CoreWebView2DownloadState / CoreWebView2DownloadInterruptReason), so Core stays free of WebView2.
/// </summary>
public static class BrowserDownloadStatus
{
    /// <summary>
    /// "InProgress" → Downloading, "Completed" → Completed. "Interrupted" depends on the reason: the user's pause or
    /// cancel; network trouble, shutdown or a crash → Interrupted (often resumable); anything else (disk, server,
    /// security scan) → Failed. The error is a short sentence for the list, or null.
    /// </summary>
    public static (DownloadState State, string? Error) Map(string state, string? interruptReason) => state switch
    {
        "InProgress" => (DownloadState.Downloading, null),
        "Completed" => (DownloadState.Completed, null),
        _ => interruptReason switch
        {
            "UserPaused" => (DownloadState.Paused, null),
            "UserCanceled" => (DownloadState.Cancelled, null),
            "NetworkFailed" or "NetworkTimeout" or "NetworkDisconnected" or "NetworkServerDown"
                or "UserShutdown" or "DownloadProcessCrashed" or "FileTransientError" or "None" or null
                => (DownloadState.Interrupted, Describe(interruptReason)),
            _ => (DownloadState.Failed, Describe(interruptReason)),
        },
    };

    /// <summary>A short explanation of an interrupt reason.</summary>
    public static string Describe(string? interruptReason) => interruptReason switch
    {
        "NetworkFailed" => "Network error",
        "NetworkTimeout" => "The connection timed out",
        "NetworkDisconnected" => "No internet connection",
        "NetworkServerDown" => "The server is unreachable",
        "NetworkInvalidRequest" => "The request was refused",
        "FileFailed" => "Couldn't save the file",
        "FileAccessDenied" => "No permission to save in this folder",
        "FileNoSpace" => "The disk is full",
        "FileNameTooLong" => "The file name or path is too long",
        "FileTooLarge" => "The file is too large for this drive",
        "FileMalicious" => "Blocked: the file may be harmful",
        "FileTransientError" => "The file was busy; try again",
        "FileBlockedByPolicy" => "Blocked by a system policy",
        "FileSecurityCheckFailed" => "The security scan failed",
        "FileTooShort" => "The file is incomplete",
        "FileHashMismatch" => "The file didn't match what was expected",
        "ServerFailed" => "Server error",
        "ServerNoRange" => "The server can't continue this download",
        "ServerBadContent" => "The server sent bad data",
        "ServerUnauthorized" => "The server needs you to sign in",
        "ServerCertificateProblem" => "The server's certificate isn't trusted",
        "ServerForbidden" => "The server refused access",
        "ServerUnexpectedResponse" => "Unexpected server response",
        "ServerContentLengthMismatch" => "The server sent a different size than announced",
        "ServerCrossOriginRedirect" => "The server redirected somewhere unexpected",
        "UserShutdown" => "AJBrowser was closed during the download",
        "DownloadProcessCrashed" => "The browser engine stopped",
        _ => "Stopped",
    };
}
