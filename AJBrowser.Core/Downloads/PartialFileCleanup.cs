namespace AJBrowser.Core.Downloads;

/// <summary>A leftover partial download file.</summary>
public sealed record StalePartialFile(string Path, long Bytes, DateTime LastWriteUtc);

/// <summary>
/// Finds old <c>.ajpart</c> files (unfinished app downloads) that no download in the list will ever continue, so the
/// app can offer to delete them at startup (plan Phase 4c). Only the top level of the given folders is searched, and
/// only AJBrowser's own extension: browser downloads use the engine's own temporary files, which it cleans up.
/// </summary>
public static class PartialFileCleanup
{
    public static readonly TimeSpan DefaultAge = TimeSpan.FromDays(7);

    /// <param name="folders">Download folders to look in (missing ones are skipped).</param>
    /// <param name="downloads">The download list: partial files of app downloads that can still continue are kept.</param>
    /// <param name="olderThan">Only files not written to for at least this long.</param>
    public static IReadOnlyList<StalePartialFile> Find(IEnumerable<string> folders, IEnumerable<DownloadItem> downloads, TimeSpan olderThan, DateTime nowUtc)
    {
        var keep = new HashSet<string>(
            downloads.Where(CanStillContinue).Select(d => System.IO.Path.GetFullPath(FileNamer.PartialPathFor(d.FilePath))),
            StringComparer.OrdinalIgnoreCase);

        var found = new List<StalePartialFile>();
        foreach (var folder in folders.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(f))).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*" + FileNamer.PartialExtension, SearchOption.TopDirectoryOnly).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                // EnumerateFiles' "*.ajpart" also matches "x.ajpart_old" on some file systems (8.3 names): check exactly.
                if (!file.EndsWith(FileNamer.PartialExtension, StringComparison.OrdinalIgnoreCase) || keep.Contains(System.IO.Path.GetFullPath(file)))
                {
                    continue;
                }

                try
                {
                    var info = new FileInfo(file);
                    if (nowUtc - info.LastWriteTimeUtc >= olderThan)
                    {
                        found.Add(new StalePartialFile(info.FullName, info.Length, info.LastWriteTimeUtc));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Gone or locked: skip it.
                }
            }
        }

        return found;
    }

    /// <summary>Deletes the files; returns how many were removed (locked or vanished ones are skipped).</summary>
    public static int Delete(IEnumerable<StalePartialFile> files)
    {
        var deleted = 0;
        foreach (var file in files)
        {
            try
            {
                File.Delete(file.Path);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use or no permission: leave it.
            }
        }

        return deleted;
    }

    /// <summary>App downloads that may still pick up their partial file (paused, interrupted, failed, waiting, running).</summary>
    private static bool CanStillContinue(DownloadItem item) =>
        item.Kind == DownloadKind.App && item.State is not (DownloadState.Completed or DownloadState.Cancelled);
}
