using AJBrowser.Core.Downloads;
using AJBrowser.Core.Media.Dash;
using AJBrowser.Core.Media.Hls;
using AJBrowser.Core.Media.YtDlp;

namespace AJBrowser.Core.Media;

/// <summary>
/// The one <see cref="IStreamDownloader"/> the download manager uses: sends each job to the HLS, DASH or yt-dlp
/// downloader by its <see cref="DownloadRequest.Source"/>. HLS and DASH share one temp folder, yt-dlp has its own
/// (one subfolder per job id in each).
/// </summary>
public sealed class StreamDownloaders(HlsDownloader hls, DashDownloader dash, YtDlpDownloader? ytdlp = null) : IStreamDownloader
{
    public Task<DownloadResult> DownloadAsync(long jobId, DownloadRequest request, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken) =>
        request.Source switch
        {
            DownloadSource.Dash => dash.DownloadAsync(jobId, request, progress, cancellationToken),
            DownloadSource.YtDlp when ytdlp is not null => ytdlp.DownloadAsync(jobId, request, progress, cancellationToken),
            DownloadSource.YtDlp => Task.FromResult(new DownloadResult(DownloadOutcome.Failed, 0, null, null, "yt-dlp downloads aren't available.")),
            _ => hls.DownloadAsync(jobId, request, progress, cancellationToken),
        };

    /// <summary>Job ids are unique across sources, so removing the id's folder in both places is safe.</summary>
    public void DeleteTemp(long jobId)
    {
        hls.DeleteTemp(jobId);
        ytdlp?.DeleteTemp(jobId);
    }
}
