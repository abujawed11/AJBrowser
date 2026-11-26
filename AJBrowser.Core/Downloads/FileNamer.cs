using System.Net.Http.Headers;

namespace AJBrowser.Core.Downloads;

/// <summary>
/// Turns server-provided names into safe Windows file names and picks a free path, <c>name (1).ext</c> style
/// (plan §6.1). A download is written to <c>name.ext.ajpart</c> first, so a name counts as taken if either exists.
/// <para>
/// <see cref="ReservePath"/> also remembers names it handed out, so two downloads starting at the same moment
/// never get the same file. Thread-safe.
/// </para>
/// </summary>
public sealed class FileNamer
{
    public const string PartialExtension = ".ajpart";
    public const string FallbackName = "download";

    /// <summary>File names longer than this are shortened (the extension is kept). Leaves room for " (99)" and ".ajpart".</summary>
    public const int MaxNameLength = 180;

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly Dictionary<string, string> ExtensionsByMediaType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["video/mp4"] = ".mp4", ["video/webm"] = ".webm", ["video/quicktime"] = ".mov", ["video/x-matroska"] = ".mkv",
        ["audio/mp4"] = ".m4a", ["audio/mpeg"] = ".mp3", ["audio/webm"] = ".weba", ["audio/ogg"] = ".ogg",
        ["application/pdf"] = ".pdf", ["application/zip"] = ".zip", ["application/x-msdownload"] = ".exe",
        ["image/png"] = ".png", ["image/jpeg"] = ".jpg", ["image/gif"] = ".gif", ["image/webp"] = ".webp",
        ["text/plain"] = ".txt", ["text/html"] = ".html", ["application/json"] = ".json",
    };

    private readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>
    /// Makes any text a safe file name: invalid characters become "_", Windows device names (CON, NUL, …) get a "_"
    /// in front, trailing dots and spaces are removed, overly long names are shortened. Never returns an empty name.
    /// </summary>
    public static string Sanitize(string? name)
    {
        var text = (name ?? string.Empty).Trim();

        // Only the last path segment counts: "../../evil.exe" or "C:\x\y.txt" must not choose a folder.
        var lastSeparator = text.LastIndexOfAny(['/', '\\']);
        if (lastSeparator >= 0)
        {
            text = text[(lastSeparator + 1)..];
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = text.Select(c => c < 32 || invalid.Contains(c) ? '_' : c).ToArray();
        text = new string(chars).Trim().TrimEnd('.', ' ');

        if (text.Length == 0 || text.All(c => c == '.' || c == '_'))
        {
            text = FallbackName;
        }

        var stem = Path.GetFileNameWithoutExtension(text);
        var extension = Path.GetExtension(text);
        if (ReservedDeviceNames.Contains(stem) || ReservedDeviceNames.Contains(stem.Split('.')[0]))
        {
            stem = "_" + stem;
        }

        if (extension.Length > 16)
        {
            // Not a real extension (for example a long word after a dot): keep it as part of the name.
            stem += extension;
            extension = string.Empty;
        }

        if (stem.Length + extension.Length > MaxNameLength)
        {
            stem = stem[..Math.Max(1, MaxNameLength - extension.Length)].TrimEnd('.', ' ');
        }

        return stem + extension;
    }

    /// <summary>
    /// The best name for a download: the server's Content-Disposition file name, else the last part of the URL path,
    /// else "download". Adds an extension from the content type when the name has none.
    /// </summary>
    public static string Suggest(Uri url, string? contentDisposition = null, string? contentType = null)
    {
        string? name = null;
        if (!string.IsNullOrWhiteSpace(contentDisposition) && ContentDispositionHeaderValue.TryParse(contentDisposition, out var disposition))
        {
            // FileNameStar is the RFC 5987 form (filename*=UTF-8''…), which carries non-ASCII names correctly.
            name = disposition.FileNameStar ?? disposition.FileName?.Trim('"');
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            var segment = url.Segments.Length > 0 ? url.Segments[^1].TrimEnd('/') : string.Empty;
            name = Uri.UnescapeDataString(segment);
        }

        var sanitized = Sanitize(name);
        if (Path.GetExtension(sanitized).Length == 0 && MediaTypeOf(contentType) is { } mediaType
            && ExtensionsByMediaType.TryGetValue(mediaType, out var extension))
        {
            sanitized = Sanitize(sanitized + extension);
        }

        return sanitized;
    }

    /// <summary>
    /// The first free path for <paramref name="fileName"/> in <paramref name="folder"/>: "a.mp4", then "a (1).mp4",
    /// "a (2).mp4", … A name is taken if <paramref name="exists"/> says the file or its ".ajpart" exists.
    /// </summary>
    public static string UniquePath(string folder, string fileName, Func<string, bool> exists)
    {
        var safe = Sanitize(fileName);
        var stem = Path.GetFileNameWithoutExtension(safe);
        var extension = Path.GetExtension(safe);

        for (var i = 0; i < 10_000; i++)
        {
            var candidate = Path.Combine(folder, i == 0 ? safe : $"{stem} ({i}){extension}");
            if (!exists(candidate) && !exists(candidate + PartialExtension))
            {
                return candidate;
            }
        }

        // Absurdly many duplicates: fall back to a unique tag rather than looping forever.
        return Path.Combine(folder, $"{stem} ({Guid.NewGuid():N}){extension}");
    }

    /// <summary>
    /// Like <see cref="UniquePath"/> on disk, and also skips names this instance handed out and not yet released,
    /// so two downloads starting together get different files. Call <see cref="Release"/> when a download ends.
    /// </summary>
    public string ReservePath(string folder, string fileName)
    {
        lock (_lock)
        {
            var path = UniquePath(folder, fileName, p => _reserved.Contains(p) || File.Exists(p));
            _reserved.Add(path);
            return path;
        }
    }

    public void Release(string path)
    {
        lock (_lock)
        {
            _reserved.Remove(path);
        }
    }

    /// <summary>The temporary file a download is written to until it's complete.</summary>
    public static string PartialPathFor(string finalPath) => finalPath + PartialExtension;

    private static string? MediaTypeOf(string? contentType) =>
        string.IsNullOrWhiteSpace(contentType) ? null : contentType.Split(';')[0].Trim();
}
