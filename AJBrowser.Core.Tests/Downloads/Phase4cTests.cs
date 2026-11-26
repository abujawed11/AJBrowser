using AJBrowser.Core.Downloads;
using AJBrowser.Core.Settings;

namespace AJBrowser.Core.Tests.Downloads;

public sealed class PartialFileCleanupTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AJBrowserTests", Guid.NewGuid().ToString("N"));

    public PartialFileCleanupTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string MakeFile(string name, int ageDays, int bytes = 10)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, Now.AddDays(-ageDays));
        return path;
    }

    private static DownloadItem App(string finalPath, DownloadState state) => new()
    {
        Kind = DownloadKind.App,
        State = state,
        SourceUrl = "https://example.com/v.mp4",
        FilePath = finalPath,
    };

    [Fact]
    public void Finds_only_old_partial_files()
    {
        var old = MakeFile("old.mp4.ajpart", ageDays: 10, bytes: 1234);
        MakeFile("recent.mp4.ajpart", ageDays: 2);
        MakeFile("finished.mp4", ageDays: 30);

        var found = PartialFileCleanup.Find([_root], [], PartialFileCleanup.DefaultAge, Now);

        var file = Assert.Single(found);
        Assert.Equal(old, file.Path, ignoreCase: true);
        Assert.Equal(1234, file.Bytes);
    }

    [Fact]
    public void Keeps_partial_files_of_app_downloads_that_can_continue()
    {
        var paused = MakeFile("paused.mp4.ajpart", ageDays: 30);
        var cancelled = MakeFile("cancelled.mp4.ajpart", ageDays: 30);
        var downloads = new[]
        {
            App(paused[..^FileNamer.PartialExtension.Length], DownloadState.Paused),
            App(cancelled[..^FileNamer.PartialExtension.Length], DownloadState.Cancelled),
        };

        var found = PartialFileCleanup.Find([_root], downloads, PartialFileCleanup.DefaultAge, Now);

        Assert.Equal(cancelled, Assert.Single(found).Path, ignoreCase: true);
    }

    [Fact]
    public void Skips_missing_folders_duplicates_and_subfolders()
    {
        MakeFile("a.ajpart", ageDays: 30);
        var sub = Directory.CreateDirectory(Path.Combine(_root, "sub")).FullName;
        File.WriteAllText(Path.Combine(sub, "deep.ajpart"), "x");
        File.SetLastWriteTimeUtc(Path.Combine(sub, "deep.ajpart"), Now.AddDays(-30));

        var found = PartialFileCleanup.Find([_root, _root + Path.DirectorySeparatorChar, Path.Combine(_root, "nope"), ""], [], PartialFileCleanup.DefaultAge, Now);

        Assert.Single(found);
    }

    [Fact]
    public void Deletes_the_files()
    {
        MakeFile("a.ajpart", ageDays: 30);
        MakeFile("b.ajpart", ageDays: 30);
        var found = PartialFileCleanup.Find([_root], [], PartialFileCleanup.DefaultAge, Now);

        Assert.Equal(2, PartialFileCleanup.Delete(found));
        Assert.Empty(Directory.GetFiles(_root));
    }
}

public sealed class ConcurrentDownloadSettingTests
{
    [Fact]
    public void Defaults_to_the_manager_default() =>
        Assert.Equal(DownloadManager.DefaultMaxConcurrent, new AppSettings().MaxConcurrentDownloads);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(5, 5)]
    [InlineData(99, 10)]
    public void Is_kept_between_1_and_10(int stored, int expected) =>
        Assert.Equal(expected, new AppSettings { MaxConcurrentDownloads = stored }.Normalize().MaxConcurrentDownloads);
}
