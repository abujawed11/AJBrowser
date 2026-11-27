using System.Globalization;
using System.Text.RegularExpressions;

namespace AJBrowser.Core.Tools;

/// <summary>The external programs AJBrowser can install on request (plan Phase 7.5).</summary>
public enum ToolKind
{
    /// <summary>yt-dlp: site extractors for YouTube, Facebook, Instagram and 1,000+ sites (public domain).</summary>
    YtDlp,

    /// <summary>FFmpeg (LGPL build): joins picture and sound, remuxes streams.</summary>
    Ffmpeg,

    /// <summary>Deno: the JavaScript runtime yt-dlp needs for YouTube (MIT).</summary>
    Deno,
}

/// <summary>
/// Where each tool comes from and what file of its release to take. Every download is an official GitHub release asset,
/// verified against its SHA-256 before use (<see cref="ToolInstaller"/>).
/// </summary>
/// <param name="Repository">"owner/name" on GitHub.</param>
/// <param name="ReleasePath">The API path under the repository: "releases/latest" or a fixed tag.</param>
/// <param name="ExeName">The program file AJBrowser runs.</param>
/// <param name="ExtraFiles">Other program files taken from the same archive (FFmpeg: ffprobe.exe).</param>
/// <param name="VersionArguments">Arguments that make the program print its version and exit (the post-install check).</param>
/// <param name="ChecksumAsset">The release's own checksum file, used when the API gives no digest.</param>
public sealed record ToolSpec(
    ToolKind Kind,
    string DisplayName,
    string Repository,
    string ReleasePath,
    string ExeName,
    IReadOnlyList<string> ExtraFiles,
    IReadOnlyList<string> VersionArguments,
    string? ChecksumAsset,
    string Licence)
{
    /// <summary>The GitHub API address of the release to install.</summary>
    public Uri ReleaseApiUrl => new($"https://api.github.com/repos/{Repository}/{ReleasePath}");
}

public static partial class ToolCatalog
{
    public static readonly ToolSpec YtDlp = new(
        ToolKind.YtDlp, "yt-dlp", "yt-dlp/yt-dlp", "releases/latest", "yt-dlp.exe", [], ["--version"], "SHA2-256SUMS", "Unlicense (public domain)");

    public static readonly ToolSpec Ffmpeg = new(
        ToolKind.Ffmpeg, "FFmpeg", "FFmpeg-Builds/FFmpeg-Builds", "releases/tags/latest", "ffmpeg.exe", ["ffprobe.exe"], ["-version"], "checksums.sha256", "LGPL 2.1+ build");

    public static readonly ToolSpec Deno = new(
        ToolKind.Deno, "Deno", "denoland/deno", "releases/latest", "deno.exe", [], ["--version"], null, "MIT");

    public static ToolSpec For(ToolKind kind) => kind switch
    {
        ToolKind.YtDlp => YtDlp,
        ToolKind.Ffmpeg => Ffmpeg,
        _ => Deno,
    };

    /// <summary>
    /// The asset to download from a release, and the version it is. yt-dlp: the standalone x64 <c>yt-dlp.exe</c>
    /// (version = the tag, a date). Deno: the x64 Windows zip (version = the tag without "v"). FFmpeg: the newest
    /// stable <b>LGPL</b> x64 build, <c>ffmpeg-n9.0-latest-win64-lgpl-9.0.zip</c> (never GPL, never "master").
    /// Null when the release has no matching file.
    /// </summary>
    public static (GitHubAsset Asset, string Version)? SelectAsset(ToolSpec spec, GitHubRelease release)
    {
        switch (spec.Kind)
        {
            case ToolKind.YtDlp:
                return release.Assets.FirstOrDefault(a => a.Name == "yt-dlp.exe") is { } exe ? (exe, release.Tag) : null;

            case ToolKind.Deno:
                return release.Assets.FirstOrDefault(a => a.Name == "deno-x86_64-pc-windows-msvc.zip") is { } zip
                    ? (zip, release.Tag.TrimStart('v'))
                    : null;

            default:
                var best = release.Assets
                    .Select(a => (Asset: a, Match: FfmpegLgplPattern().Match(a.Name)))
                    .Where(x => x.Match.Success && x.Match.Groups["v1"].Value == x.Match.Groups["v2"].Value)
                    .Select(x => (x.Asset, Version: x.Match.Groups["v1"].Value))
                    .OrderByDescending(x => ToolVersion.Parse(x.Version), ToolVersion.Comparer)
                    .FirstOrDefault();
                return best.Asset is null ? null : (best.Asset, best.Version);
        }
    }

    [GeneratedRegex(@"^ffmpeg-n(?<v1>\d+\.\d+)-latest-win64-lgpl-(?<v2>\d+\.\d+)\.zip$", RegexOptions.CultureInvariant)]
    private static partial Regex FfmpegLgplPattern();
}

/// <summary>Version strings of the tools ("2026.11.23", "2.9.8", "9.0") compared part by part as numbers.</summary>
public static class ToolVersion
{
    public static IReadOnlyList<long> Parse(string? version) =>
        (version ?? string.Empty).TrimStart('v', 'V').Split('.', '-', '+')
            .Select(p => long.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1)
            .TakeWhile(n => n >= 0)
            .ToList();

    /// <summary>True when <paramref name="candidate"/> is a later version than <paramref name="installed"/>.</summary>
    public static bool IsNewer(string? candidate, string? installed) => Comparer.Compare(Parse(candidate), Parse(installed)) > 0;

    public static readonly IComparer<IReadOnlyList<long>> Comparer = Comparer<IReadOnlyList<long>>.Create((a, b) =>
    {
        for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            var x = i < a.Count ? a[i] : 0;
            var y = i < b.Count ? b[i] : 0;
            if (x != y)
            {
                return x.CompareTo(y);
            }
        }

        return 0;
    });
}
