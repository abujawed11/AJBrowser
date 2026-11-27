using AJBrowser.Core.Security;

namespace AJBrowser.Core.Media.YtDlp;

/// <summary>What the user can do about a yt-dlp failure; the picker shows the matching button.</summary>
public enum YtDlpFix
{
    None,
    SignIn,
    UpdateYtDlp,
    InstallDeno,
    InstallFfmpeg,
    Retry,
}

/// <summary>
/// A yt-dlp failure as a sentence for the user (never containing addresses, cookies or tokens) and a suggested fix.
/// <see cref="Detail"/>: yt-dlp's own last error/warning lines, redacted, for the log (diagnosing sign-in problems).
/// </summary>
public sealed record YtDlpProblem(string Message, YtDlpFix Fix, string? Detail = null);

/// <summary>Maps yt-dlp's <c>ERROR:</c>/<c>WARNING:</c> lines to plain sentences (plan Phase 7.5a). Pure, unit-tested.</summary>
public static class YtDlpErrors
{
    private static readonly (string[] Needles, YtDlpProblem Problem)[] Rules =
    [
        (["confirm you're not a bot", "confirm you’re not a bot", "sign in to confirm your age", "inappropriate for some users", "login required", "requires authentication",
          "you need to log in", "this content isn't available", "use --cookies", "cookies-from-browser", "registered users", "members-only", "join this channel"],
            new("This video needs you to be signed in. Sign in on the site in AJBrowser, then try again.", YtDlpFix.SignIn)),
        (["private video", "this video is private", "is a private"],
            new("This video is private.", YtDlpFix.None)),
        (["drm", "widevine", "playready"],
            new("Protected (DRM): can't be downloaded", YtDlpFix.None)),
        (["available in your country", "geo restrict", "geo-restrict", "not available from your location", "blocked it in your country"],
            new("This video isn't available in your country.", YtDlpFix.None)),
        (["video unavailable", "has been removed", "no longer available", "does not exist", "this video has been terminated", "account associated with this video has been terminated"],
            new("This video is unavailable (removed or never existed).", YtDlpFix.None)),
        (["this live event will begin", "premieres in", "is_upcoming"],
            new("This video hasn't started yet.", YtDlpFix.None)),
        (["is live", "live stream", "--live-from-start"],
            new("Live stream: not supported yet", YtDlpFix.None)),
        (["unsupported url"],
            new("yt-dlp doesn't support this page. Open the video's own page, then try again.", YtDlpFix.None)),
        (["no supported javascript runtime", "js runtime", "javascript runtime"],
            new("YouTube needs the Deno helper for this video. Install it in Settings → Downloads, then try again.", YtDlpFix.InstallDeno)),
        (["ffmpeg is not installed", "ffmpeg not found", "ffprobe and ffmpeg not found", "you have requested merging"],
            new("Joining picture and sound needs FFmpeg. Install it in Settings → Downloads, then try again.", YtDlpFix.InstallFfmpeg)),
        (["requested format is not available"],
            new("That quality is no longer offered. Open the list again and pick another.", YtDlpFix.Retry)),
        (["http error 429", "too many requests", "rate-limit", "rate limit"],
            new("The site is limiting downloads right now. Wait a few minutes, then try again.", YtDlpFix.Retry)),
        (["http error 403", "forbidden"],
            new("The site refused the download (HTTP 403). Try again; if it keeps happening, update yt-dlp.", YtDlpFix.UpdateYtDlp)),
        (["unable to extract", "nsig extraction failed", "signature extraction failed", "precondition check failed", "please report this issue",
          "confirm you are on the latest version", "update to the latest version", "unable to download api page", "failed to parse json"],
            new("The site changed and yt-dlp can't read it yet. Update yt-dlp, then try again.", YtDlpFix.UpdateYtDlp)),
        (["unable to download webpage", "getaddrinfo failed", "timed out", "connection reset", "network is unreachable", "temporary failure in name resolution"],
            new("Network problem while talking to the site. Check the connection, then try again.", YtDlpFix.Retry)),
        (["no space left on device", "not enough space"],
            new("The disk is full.", YtDlpFix.None)),
    ];

    /// <summary>
    /// The best explanation for a failed run from its stderr lines (latest error first). Unknown errors: yt-dlp's last
    /// <c>ERROR:</c> line, redacted (addresses, cookies, tokens removed), shortened.
    /// </summary>
    public static YtDlpProblem Explain(IReadOnlyList<string> errorLines) =>
        ExplainCore(errorLines) with { Detail = DetailOf(errorLines) };

    /// <summary>The last three ERROR/WARNING lines, redacted and shortened: what yt-dlp itself said.</summary>
    public static string? DetailOf(IReadOnlyList<string> errorLines)
    {
        var lines = errorLines
            .Where(l => l.StartsWith("ERROR:", StringComparison.Ordinal) || l.StartsWith("WARNING:", StringComparison.Ordinal))
            .TakeLast(3)
            .Select(l => Redactor.Text(l.Length > 300 ? l[..300] + "…" : l))
            .ToList();
        return lines.Count == 0 ? null : string.Join(" | ", lines);
    }

    private static YtDlpProblem ExplainCore(IReadOnlyList<string> errorLines)
    {
        var errors = errorLines.Where(l => l.StartsWith("ERROR:", StringComparison.Ordinal)).Reverse().ToList();

        // Without a JavaScript runtime YouTube gives fewer (or no) formats; then "format not available" really means "install Deno".
        if (MissesJsRuntime(errorLines) && errors.Any(l => l.Contains("format is not available", StringComparison.OrdinalIgnoreCase)
                                                          || l.Contains("only images are available", StringComparison.OrdinalIgnoreCase)
                                                          || l.Contains("signature", StringComparison.OrdinalIgnoreCase)))
        {
            return Rules.First(r => r.Problem.Fix == YtDlpFix.InstallDeno).Problem;
        }

        foreach (var line in errors.Concat(errorLines.Reverse()))
        {
            var lower = line.ToLowerInvariant();
            foreach (var (needles, problem) in Rules)
            {
                if (needles.Any(n => lower.Contains(n, StringComparison.Ordinal)))
                {
                    return problem;
                }
            }
        }

        var last = errors.FirstOrDefault() ?? errorLines.LastOrDefault(l => !string.IsNullOrWhiteSpace(l));
        if (last is null)
        {
            return new YtDlpProblem("yt-dlp stopped without saying why. Try again.", YtDlpFix.Retry);
        }

        var text = Redactor.Text(last.StartsWith("ERROR:", StringComparison.Ordinal) ? last[6..].Trim() : last.Trim());
        if (text.Length > 200)
        {
            text = text[..200] + "…";
        }

        return new YtDlpProblem($"yt-dlp: {text}", YtDlpFix.Retry);
    }

    /// <summary>The warning yt-dlp prints when YouTube formats may be missing for lack of a JavaScript runtime.</summary>
    public static bool MissesJsRuntime(IEnumerable<string> lines) =>
        lines.Any(l => l.Contains("No supported JavaScript runtime", StringComparison.OrdinalIgnoreCase));
}
