using System.IO.Compression;
using System.Text.Json;

namespace AJBrowser.Core.AdBlocking;

/// <summary>
/// A browser extension shipped with the app as its official release .zip (unmodified), for example
/// <c>Extensions\uBOLite_2026.1124.1811.chromium.zip</c> next to AJBrowser.exe.
/// </summary>
public sealed record ExtensionPackage(string ZipPath, Version Version)
{
    /// <summary>Folder name used for this version once unpacked.</summary>
    public string FolderName => ExtensionVersion.Format(Version);

    /// <summary>
    /// Finds the newest valid package matching <paramref name="searchPattern"/> in <paramref name="folder"/>.
    /// Returns null if the folder or a usable package is missing. Broken zips are skipped, never thrown.
    /// </summary>
    public static ExtensionPackage? FindNewest(string folder, string searchPattern)
    {
        if (!Directory.Exists(folder))
        {
            return null;
        }

        ExtensionPackage? best = null;
        foreach (var zip in Directory.EnumerateFiles(folder, searchPattern))
        {
            var version = TryReadVersion(zip);
            if (version is not null && (best is null || version > best.Version))
            {
                best = new ExtensionPackage(zip, version);
            }
        }

        return best;
    }

    /// <summary>Reads "version" from the zip's top-level manifest.json, or null if the zip isn't a valid extension.</summary>
    public static Version? TryReadVersion(string zipPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var manifest = archive.GetEntry("manifest.json");
            if (manifest is null)
            {
                return null;
            }

            using var stream = manifest.Open();
            using var json = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return json.RootElement.ValueKind == JsonValueKind.Object
                   && json.RootElement.TryGetProperty("version", out var version)
                   && version.ValueKind == JsonValueKind.String
                ? ExtensionVersion.TryParse(version.GetString())
                : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Unpacks into <c>parentFolder\&lt;version&gt;</c> and returns that folder. If it is already unpacked
    /// (has a manifest.json) it is reused. Extraction goes to a temporary folder first and is renamed at the end,
    /// so a crash halfway never leaves a half-filled folder that looks complete.
    /// </summary>
    public string UnpackTo(string parentFolder)
    {
        var target = Path.Combine(parentFolder, FolderName);
        if (File.Exists(Path.Combine(target, "manifest.json")))
        {
            return target;
        }

        Directory.CreateDirectory(parentFolder);
        var partial = target + ".partial";
        DeleteDirectoryIfExists(partial);
        DeleteDirectoryIfExists(target); // exists but has no manifest: left over from something broken

        // ExtractToDirectory refuses entries that would escape the folder ("zip slip").
        ZipFile.ExtractToDirectory(ZipPath, partial);
        Directory.Move(partial, target);
        return target;
    }

    /// <summary>
    /// Best effort: deletes the other version folders (and leftover ".partial" folders) under
    /// <paramref name="parentFolder"/>, keeping <paramref name="keepFolder"/> and any plain files.
    /// Returns the folders that couldn't be deleted (for example still locked by the browser).
    /// </summary>
    public static IReadOnlyList<string> DeleteOtherVersions(string parentFolder, string keepFolder)
    {
        var failed = new List<string>();
        if (!Directory.Exists(parentFolder))
        {
            return failed;
        }

        var keep = Path.GetFullPath(keepFolder).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var directory in Directory.EnumerateDirectories(parentFolder))
        {
            if (string.Equals(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar), keep, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = Path.GetFileName(directory);
            var isVersionFolder = ExtensionVersion.TryParse(name.EndsWith(".partial", StringComparison.Ordinal) ? name[..^".partial".Length] : name) is not null;
            if (!isVersionFolder)
            {
                continue; // not ours: leave it alone
            }

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(directory);
            }
        }

        return failed;
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
