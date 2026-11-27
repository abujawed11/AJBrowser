namespace AJBrowser.Core.Media.YtDlp;

/// <summary>Everything a yt-dlp download run needs (plan Phase 7.5a). Paths are absolute and local.</summary>
/// <param name="FileNameWithoutExtension">From <see cref="Downloads.FileNamer"/>; yt-dlp adds the real extension.</param>
/// <param name="TempFolder">Per job (<c>%LOCALAPPDATA%\AJBrowser\Temp\ytdlp\{id}</c>): yt-dlp's partial files, kept for resuming.</param>
/// <param name="CookieFile">A Netscape cookie file in the temp folder (<see cref="NetscapeCookieFile"/>), or null.</param>
public sealed record YtDlpDownloadOptions(
    Uri PageUrl,
    string FormatSelector,
    string? MergeFormat,
    string OutputFolder,
    string FileNameWithoutExtension,
    string TempFolder,
    string? FfmpegPath,
    string? DenoPath,
    string? CookieFile,
    string? UserAgent);

/// <summary>
/// Builds yt-dlp argument lists. Always passed through <c>ProcessStartInfo.ArgumentList</c>, never as one string, so
/// nothing in a title or address can become an extra option; the page address goes after <c>--</c>. Every run ignores
/// any yt-dlp config files on the PC (<c>--ignore-config</c>) so the user's own yt-dlp setup can't change what
/// AJBrowser does, and never downloads playlists.
/// </summary>
public static class YtDlpArguments
{
    /// <summary>Marks AJBrowser's progress lines in yt-dlp's output.</summary>
    public const string ProgressPrefix = "ajb-progress";

    /// <summary>Marks the line with the finished file's path.</summary>
    public const string FilePrefix = "ajb-file:";

    /// <summary><c>-J</c>: the video's information as one JSON line, nothing downloaded.</summary>
    public static IReadOnlyList<string> Inspect(Uri pageUrl, string? denoPath, string? cookieFile, string? userAgent)
    {
        RequireHttp(pageUrl);
        // Warnings stay on: "No supported JavaScript runtime" is one, and it explains missing YouTube formats.
        var args = new List<string> { "--ignore-config", "--no-playlist", "--encoding", "utf-8", "-J" };
        AddCommon(args, denoPath, cookieFile, userAgent);
        args.Add("--");
        args.Add(pageUrl.AbsoluteUri);
        return args;
    }

    /// <summary>Downloads the chosen formats into the output folder, printing progress and the final path.</summary>
    public static IReadOnlyList<string> Download(YtDlpDownloadOptions o)
    {
        RequireHttp(o.PageUrl);
        if (string.IsNullOrWhiteSpace(o.FormatSelector) || o.FormatSelector.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '_' or '.')))
        {
            throw new ArgumentException("Invalid format selector.", nameof(o));
        }

        if (!Path.IsPathFullyQualified(o.OutputFolder) || !Path.IsPathFullyQualified(o.TempFolder))
        {
            throw new ArgumentException("Folders must be absolute.", nameof(o));
        }

        var args = new List<string>
        {
            // --encoding: yt-dlp.exe ignores PYTHONIOENCODING/PYTHONUTF8 and, writing to a pipe, drops every character
            // outside the Windows code page (Urdu, Hindi, emoji…) from its output, including the final file path (F23).
            "--ignore-config", "--no-playlist", "--encoding", "utf-8", "--newline", "--no-mtime", "--no-overwrites", "--continue",
            "--no-simulate", "--progress", // --print below would otherwise imply --simulate and --quiet
            "--progress-template", $"download:{ProgressPrefix} %(progress.downloaded_bytes)s %(progress.total_bytes)s %(progress.total_bytes_estimate)s %(progress.speed)s %(progress.eta)s %(info.format_id)s",
            "--print", $"after_move:{FilePrefix}%(filepath)s",
            "-f", o.FormatSelector,
        };

        if (o.MergeFormat is { } merge)
        {
            args.AddRange(["--merge-output-format", merge]);
        }

        if (o.FfmpegPath is { } ffmpeg)
        {
            args.AddRange(["--ffmpeg-location", ffmpeg]);
        }

        AddCommon(args, o.DenoPath, o.CookieFile, o.UserAgent);
        args.AddRange(["-P", $"home:{o.OutputFolder}", "-P", $"temp:{o.TempFolder}", "-o", OutputTemplate(o.FileNameWithoutExtension)]);
        args.Add("--");
        args.Add(o.PageUrl.AbsoluteUri);
        return args;
    }

    /// <summary>"Name.%(ext)s" with '%' in the name escaped (yt-dlp templates use '%' for fields).</summary>
    public static string OutputTemplate(string fileNameWithoutExtension) =>
        fileNameWithoutExtension.Replace("%", "%%", StringComparison.Ordinal) + ".%(ext)s";

    private static void AddCommon(List<string> args, string? denoPath, string? cookieFile, string? userAgent)
    {
        if (denoPath is { } deno)
        {
            args.AddRange(["--js-runtimes", $"deno:{deno}"]);
        }

        if (cookieFile is { } cookies)
        {
            args.AddRange(["--cookies", cookies]);
        }

        if (!string.IsNullOrWhiteSpace(userAgent) && !userAgent.Any(char.IsControl))
        {
            args.AddRange(["--user-agent", userAgent]);
        }
    }

    private static void RequireHttp(Uri url)
    {
        if (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("Only web pages (http/https) can be given to yt-dlp.", nameof(url));
        }
    }
}
