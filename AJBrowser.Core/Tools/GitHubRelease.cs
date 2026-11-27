using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AJBrowser.Core.Tools;

/// <summary>One file of a GitHub release. <see cref="Sha256"/> is lower-case hex from the API's <c>digest</c>, when given.</summary>
public sealed record GitHubAsset(string Name, Uri DownloadUrl, long Size, string? Sha256);

/// <summary>A GitHub release, reduced to what the installer needs.</summary>
public sealed record GitHubRelease(string Tag, DateTimeOffset? PublishedAt, IReadOnlyList<GitHubAsset> Assets)
{
    /// <summary>
    /// Reads the GitHub REST API's release JSON for <paramref name="repository"/>. Only assets whose download address
    /// is that repository's own <c>https://github.com/{owner}/{name}/releases/download/…</c> are kept, so a tampered
    /// or unexpected response can't point the installer anywhere else. Throws <see cref="FormatException"/> when the
    /// JSON isn't a release.
    /// </summary>
    public static GitHubRelease Parse(string json, string repository)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String)
            {
                throw new FormatException("Not a GitHub release.");
            }

            var prefix = $"/{repository}/releases/download/";
            var assets = new List<GitHubAsset>();
            if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in list.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                    var url = asset.TryGetProperty("browser_download_url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                    var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
                    var digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                    if (name is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                        || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com"
                        || !uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    assets.Add(new GitHubAsset(name, uri, size, Checksums.NormalizeSha256(digest?.StartsWith("sha256:", StringComparison.Ordinal) == true ? digest[7..] : null)));
                }
            }

            DateTimeOffset? published = root.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(p.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
            return new GitHubRelease(tag.GetString()!, published, assets);
        }
        catch (JsonException ex)
        {
            throw new FormatException("Not a GitHub release.", ex);
        }
    }
}

/// <summary>Reads the checksum files tools publish next to their releases.</summary>
public static partial class Checksums
{
    /// <summary>
    /// The SHA-256 of <paramref name="fileName"/> from a checksum file. Understands GNU style lines
    /// (<c>hash  name</c> or <c>hash *name</c>; yt-dlp's SHA2-256SUMS, FFmpeg's checksums.sha256) and PowerShell
    /// <c>Get-FileHash</c> output with one <c>Hash : …</c> line (Deno's .sha256sum files). Null when not found.
    /// </summary>
    public static string? Find(string text, string fileName)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var match = GnuLine().Match(line);
            if (match.Success && match.Groups["name"].Value == fileName)
            {
                return NormalizeSha256(match.Groups["hash"].Value);
            }
        }

        var hashLines = text.Split('\n').Select(l => PowerShellHashLine().Match(l.Trim())).Where(m => m.Success).ToList();
        return hashLines.Count == 1 ? NormalizeSha256(hashLines[0].Groups["hash"].Value) : null;
    }

    /// <summary>64 hex characters → lower case; anything else → null.</summary>
    public static string? NormalizeSha256(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit) ? value.ToLowerInvariant() : null;

    [GeneratedRegex(@"^(?<hash>[0-9A-Fa-f]{64}) [ *](?<name>\S.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex GnuLine();

    [GeneratedRegex(@"^Hash\s*:\s*(?<hash>[0-9A-Fa-f]{64})$", RegexOptions.CultureInvariant)]
    private static partial Regex PowerShellHashLine();
}
