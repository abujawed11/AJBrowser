using AJBrowser.Core.Downloads;
using AJBrowser.Core.Media.Dash;
using AJBrowser.Core.Media.Hls;

namespace AJBrowser.Core.Media;

/// <summary>
/// The one <see cref="IStreamDownloader"/> the download manager uses: sends each job to the HLS or DASH downloader by
/// its <see cref="DownloadRequest.Source"/>. Both share one temp folder (one subfolder per job id).
/// </summary>
public sealed class StreamDownloaders(HlsDownloader hls, DashDownloader dash) : IStreamDownloader
{
    public Task<DownloadResult> DownloadAsync(long jobId, DownloadRequest request, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken) =>
        request.Source == DownloadSource.Dash
            ? dash.DownloadAsync(jobId, request, progress, cancellationToken)
            : hls.DownloadAsync(jobId, request, progress, cancellationToken);

    public void DeleteTemp(long jobId) => hls.DeleteTemp(jobId);
}
