using System.Globalization;
using AJBrowser.Core.Downloads;
using AJBrowser.Core.Navigation;

namespace AJBrowser.Core.Tests.Downloads;

public sealed class DownloadTextTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static DownloadItem Item(DownloadState state, long received = 0, long? total = null) => new()
    {
        Kind = DownloadKind.Browser,
        State = state,
        SourceUrl = "https://example.com/file.zip?token=secret",
        FilePath = @"C:\Downloads\file.zip",
        ReceivedBytes = received,
        TotalBytes = total,
    };

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(-5, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(12_897_484, "12.3 MB")]
    [InlineData(478_150_656, "456 MB")]
    [InlineData(1_148_903_751, "1.07 GB")]
    public void Formats_sizes(long bytes, string expected) => Assert.Equal(expected, DownloadText.Size(bytes, Invariant));

    [Fact]
    public void Formats_speed() => Assert.Equal("1.5 MB/s", DownloadText.Speed(1.5 * 1024 * 1024, Invariant));

    [Theory]
    [InlineData(0.2, "1 s left")]
    [InlineData(42, "42 s left")]
    [InlineData(61, "2 min left")]
    [InlineData(3600, "1 h left")]
    [InlineData(3900, "1 h 5 min left")]
    [InlineData(3 * 86400, "3 days left")]
    public void Formats_time_left(double seconds, string expected) =>
        Assert.Equal(expected, DownloadText.Remaining(TimeSpan.FromSeconds(seconds), Invariant));

    [Fact]
    public void Downloading_shows_progress_speed_and_time_left()
    {
        var item = Item(DownloadState.Downloading, 1024 * 1024, 10 * 1024 * 1024) with { BytesPerSecond = 1024 * 1024, Remaining = TimeSpan.FromSeconds(9) };
        Assert.Equal("1 MB of 10 MB · 1 MB/s · 9 s left", DownloadText.Status(item, culture: Invariant));
    }

    [Fact]
    public void Downloading_with_unknown_size_shows_received_only() =>
        Assert.Equal("2 KB", DownloadText.Status(Item(DownloadState.Downloading, 2048), culture: Invariant));

    [Fact]
    public void Paused_completed_failed_and_others()
    {
        Assert.Equal("Paused · 1 KB of 2 KB", DownloadText.Status(Item(DownloadState.Paused, 1024, 2048), culture: Invariant));
        Assert.Equal("2 KB", DownloadText.Status(Item(DownloadState.Completed, 2048, 2048), culture: Invariant));
        Assert.Equal("Moved or deleted", DownloadText.Status(Item(DownloadState.Completed, 2048), fileMissing: true, culture: Invariant));
        Assert.Equal("Failed · The disk is full", DownloadText.Status(Item(DownloadState.Failed) with { Error = "The disk is full" }, culture: Invariant));
        Assert.Equal("Interrupted", DownloadText.Status(Item(DownloadState.Interrupted), culture: Invariant));
        Assert.Equal("Cancelled", DownloadText.Status(Item(DownloadState.Cancelled), culture: Invariant));
        Assert.Equal("Waiting…", DownloadText.Status(Item(DownloadState.Queued), culture: Invariant));
    }

    [Fact]
    public void Status_never_contains_the_url() =>
        Assert.All(Enum.GetValues<DownloadState>(), s => Assert.DoesNotContain("secret", DownloadText.Status(Item(s), culture: Invariant)));
}

public sealed class BrowserDownloadStatusTests
{
    [Theory]
    [InlineData("InProgress", "None", DownloadState.Downloading)]
    [InlineData("Completed", "None", DownloadState.Completed)]
    [InlineData("Interrupted", "UserPaused", DownloadState.Paused)]
    [InlineData("Interrupted", "UserCanceled", DownloadState.Cancelled)]
    [InlineData("Interrupted", "NetworkFailed", DownloadState.Interrupted)]
    [InlineData("Interrupted", "NetworkDisconnected", DownloadState.Interrupted)]
    [InlineData("Interrupted", "UserShutdown", DownloadState.Interrupted)]
    [InlineData("Interrupted", "FileNoSpace", DownloadState.Failed)]
    [InlineData("Interrupted", "FileMalicious", DownloadState.Failed)]
    [InlineData("Interrupted", "ServerForbidden", DownloadState.Failed)]
    [InlineData("Interrupted", "SomethingNew", DownloadState.Failed)]
    public void Maps_runtime_states(string state, string reason, DownloadState expected) =>
        Assert.Equal(expected, BrowserDownloadStatus.Map(state, reason).State);

    [Fact]
    public void User_actions_have_no_error_and_failures_explain_themselves()
    {
        Assert.Null(BrowserDownloadStatus.Map("Interrupted", "UserPaused").Error);
        Assert.Null(BrowserDownloadStatus.Map("Interrupted", "UserCanceled").Error);
        Assert.Equal("The disk is full", BrowserDownloadStatus.Map("Interrupted", "FileNoSpace").Error);
        Assert.Equal("Stopped", BrowserDownloadStatus.Describe("SomethingNew"));
    }

    [Fact]
    public void Every_mapped_state_is_reachable_from_downloading() =>
        Assert.All(
            new[] { "UserPaused", "UserCanceled", "NetworkFailed", "FileNoSpace" },
            reason => Assert.True(DownloadStateMachine.CanMove(DownloadState.Downloading, BrowserDownloadStatus.Map("Interrupted", reason).State)));

    [Fact]
    public void Downloads_page_has_an_internal_address() =>
        Assert.Equal(InternalPage.Downloads, InternalPages.TryParse("ajbrowser://downloads"));
}
