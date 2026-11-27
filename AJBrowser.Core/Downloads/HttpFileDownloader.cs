using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

namespace AJBrowser.Core.Downloads;

/// <summary>How an app download attempt ended.</summary>
public enum DownloadOutcome
{
    Completed,

    /// <summary>The caller cancelled (pause or cancel; the caller decides whether to keep the partial file).</summary>
    Cancelled,

    Failed,
}

/// <summary>What to download and where. <see cref="Headers"/> are extra request headers (for example Referer, cookies in Phase 5).</summary>
/// <remarks>
/// <see cref="AudioUrl"/>: an HLS stream's separate sound track (its media playlist), merged with FFmpeg.
/// <see cref="VideoTrackId"/>/<see cref="AudioTrackId"/>: a DASH stream's chosen representations (<see cref="Url"/> is the manifest).
/// <see cref="ExpectedBytes"/>: the size the picker showed (exact or estimated), shown as the total until the downloader
/// reports a better one (yt-dlp only knows the sound part's size once it starts it, streams only after their first piece).
/// </remarks>
public sealed record DownloadRequest(
    Uri Url,
    string TargetPath,
    IReadOnlyDictionary<string, string>? Headers = null,
    DownloadSource Source = DownloadSource.File,
    Uri? AudioUrl = null,
    string? VideoTrackId = null,
    string? AudioTrackId = null,
    long? ExpectedBytes = null);

public sealed record DownloadProgress(long ReceivedBytes, long? TotalBytes);

/// <summary>The result of <see cref="HttpFileDownloader.DownloadAsync"/>. <see cref="Error"/> never contains the URL.</summary>
/// <remarks><see cref="FinalPath"/>: set when the file ended up under a different name (an HLS stream saved as .ts when it couldn't become .mp4).</remarks>
public sealed record DownloadResult(DownloadOutcome Outcome, long ReceivedBytes, long? TotalBytes, ResumeInfo? Resume, string? Error = null, string? FinalPath = null);

/// <summary>
/// Downloads app jobs that aren't a single file (HLS streams, Phase 6; DASH streams, Phase 7). Pausing keeps its temporary pieces,
/// so the next run continues; <see cref="DeleteTemp"/> throws them away (cancel).
/// </summary>
public interface IStreamDownloader
{
    Task<DownloadResult> DownloadAsync(long jobId, DownloadRequest request, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken);

    void DeleteTemp(long jobId);
}

/// <summary>
/// Downloads one file over HTTP for app-managed jobs (no UI, unit-tested with a fake handler).
/// <list type="bullet">
/// <item>Writes to <c>target.ajpart</c> and renames to the target only when complete, so a partial file is never
/// mistaken for a finished one.</item>
/// <item>Resumes with <c>Range</c> + <c>If-Range</c> when the server accepts ranges and gave an ETag or Last-Modified;
/// if the server ignores the range (200) or the file changed, it starts again from zero.</item>
/// <item>Retries network errors, timeouts, 408/429/5xx with growing waits (1, 2, 4 s), continuing where it stopped
/// when it can. A stall (no data for <see cref="StallTimeout"/>) counts as a network error.</item>
/// <item>Cancellation stops quickly and keeps the partial file (for pause); <see cref="DeletePartial"/> removes it (for cancel).</item>
/// </list>
/// </summary>
public sealed class HttpFileDownloader
{
    /// <summary>At least 81,920 bytes: the size .NET's own stream copy uses, and above the large-object threshold for fewer allocations.</summary>
    public const int BufferSize = 81_920;

    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="http">Should have an infinite (or very long) Timeout: big files take long; stalls are caught separately.</param>
    /// <param name="delay">Waits between retries; tests pass one that doesn't really wait.</param>
    public HttpFileDownloader(HttpClient http, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Attempts after the first one, for transient problems.</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>No data for this long = a dead connection (retried).</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Downloads <paramref name="request"/>. <paramref name="previous"/> is what an earlier attempt learned about the
    /// server (for resuming an existing partial file). Never throws for network, HTTP or file problems: they come back
    /// as <see cref="DownloadOutcome.Failed"/> with a short message.
    /// </summary>
    public async Task<DownloadResult> DownloadAsync(DownloadRequest request, ResumeInfo? previous, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var partialPath = FileNamer.PartialPathFor(request.TargetPath);
        var resume = previous;
        long? total = null;
        string? lastError = null;

        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            if (attempt > 0)
            {
                try
                {
                    await _delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return new DownloadResult(DownloadOutcome.Cancelled, PartialLength(partialPath), total, resume);
                }
            }

            var step = await TryOnceAsync(request, partialPath, resume, progress, cancellationToken).ConfigureAwait(false);
            resume = step.Resume ?? resume;
            total = step.Total ?? total;

            switch (step.Kind)
            {
                case StepKind.Completed:
                    try
                    {
                        File.Move(partialPath, request.TargetPath, overwrite: false);
                    }
                    catch (IOException ex)
                    {
                        return new DownloadResult(DownloadOutcome.Failed, step.Received, total, resume, $"Couldn't save the file: {ex.Message}");
                    }

                    return new DownloadResult(DownloadOutcome.Completed, step.Received, total ?? step.Received, resume);

                case StepKind.Cancelled:
                    return new DownloadResult(DownloadOutcome.Cancelled, PartialLength(partialPath), total, resume);

                case StepKind.Fatal:
                    return new DownloadResult(DownloadOutcome.Failed, PartialLength(partialPath), total, resume, step.Error);

                case StepKind.Retry:
                    lastError = step.Error;
                    break;
            }
        }

        return new DownloadResult(DownloadOutcome.Failed, PartialLength(partialPath), total, resume, lastError ?? "The download kept failing.");
    }

    /// <summary>Removes the partial file of a cancelled download (ignores a missing or locked file).</summary>
    public static void DeletePartial(string targetPath)
    {
        try
        {
            File.Delete(FileNamer.PartialPathFor(targetPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind; startup cleanup (Phase 4c) removes old .ajpart files.
        }
    }

    private enum StepKind
    {
        Completed,
        Cancelled,
        Retry,
        Fatal,
    }

    private sealed record Step(StepKind Kind, long Received, long? Total, ResumeInfo? Resume, string? Error = null);

    private async Task<Step> TryOnceAsync(DownloadRequest request, string partialPath, ResumeInfo? resume, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var existing = PartialLength(partialPath);
        var tryRange = existing > 0 && resume is { CanResume: true };

        using var message = new HttpRequestMessage(HttpMethod.Get, request.Url);
        if (request.Headers is not null)
        {
            foreach (var (name, value) in request.Headers)
            {
                message.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (tryRange)
        {
            message.Headers.Range = new RangeHeaderValue(existing, null);

            // If-Range: "only send the rest if the file is still the same one; otherwise send it all" (200).
            if (resume!.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out var tag) && !tag.IsWeak)
            {
                message.Headers.IfRange = new RangeConditionHeaderValue(tag);
            }
            else if (resume.LastModified is { } lastModified && DateTimeOffset.TryParse(lastModified, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            {
                message.Headers.IfRange = new RangeConditionHeaderValue(date);
            }
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new Step(StepKind.Cancelled, existing, null, resume);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return new Step(StepKind.Retry, existing, null, resume, "Network error: couldn't reach the server.");
        }

        using (response)
        {
            var learned = ReadResumeInfo(response) ?? resume;
            var status = (int)response.StatusCode;

            if (status == 416 && existing > 0)
            {
                // Our partial file doesn't fit the server's file any more: start over.
                DeleteFile(partialPath);
                return new Step(StepKind.Retry, 0, null, learned, "The server's file changed; starting again.");
            }

            if (status is 408 or 429 || status >= 500)
            {
                return new Step(StepKind.Retry, existing, null, learned, $"The server is busy or failing (HTTP {status}).");
            }

            if (status is 401 or 403 or 410)
            {
                // Video sites sign their links and the signature expires (plan Phase 5): say what to do about it.
                return new Step(StepKind.Fatal, existing, null, learned, $"The link has expired or access was refused (HTTP {status}). Play the video again, then Retry.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new Step(StepKind.Fatal, existing, null, learned, $"The server refused the download (HTTP {status}).");
            }

            long startAt;
            long? total;
            if (response.StatusCode == HttpStatusCode.PartialContent && tryRange
                && response.Content.Headers.ContentRange is { HasRange: true, From: { } from } range && from == existing)
            {
                startAt = existing; // continue the partial file
                total = range.Length;
            }
            else
            {
                startAt = 0; // full body (server ignored the range, or the file changed): overwrite
                total = response.Content.Headers.ContentLength;
            }

            return await CopyBodyAsync(response, partialPath, startAt, total, learned, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Step> CopyBodyAsync(HttpResponseMessage response, string partialPath, long startAt, long? total, ResumeInfo? resume, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var received = startAt;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            FileStream file;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(partialPath))!);
                file = new FileStream(partialPath, startAt > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, BufferSize, FileOptions.Asynchronous);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new Step(StepKind.Fatal, received, total, resume, $"Couldn't write the file: {ex.Message}");
            }

            await using (file.ConfigureAwait(false))
            {
                Stream body;
                try
                {
                    body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return new Step(StepKind.Cancelled, received, total, resume);
                }

                await using (body.ConfigureAwait(false))
                {
                    progress?.Report(new DownloadProgress(received, total));
                    while (true)
                    {
                        int read;
                        try
                        {
                            stall.CancelAfter(StallTimeout);
                            read = await body.ReadAsync(buffer.AsMemory(0, BufferSize), stall.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            return new Step(StepKind.Cancelled, received, total, resume);
                        }
                        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException)
                        {
                            await file.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                            return new Step(StepKind.Retry, received, total, resume, "The connection dropped.");
                        }

                        if (read == 0)
                        {
                            break;
                        }

                        try
                        {
                            await file.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (IOException ex)
                        {
                            // Disk full, folder deleted, device removed: retrying won't help.
                            return new Step(StepKind.Fatal, received, total, resume, $"Couldn't write the file: {ex.Message}");
                        }

                        received += read;
                        progress?.Report(new DownloadProgress(received, total));
                    }

                    await file.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }

            if (total is { } expected && received != expected)
            {
                // The body ended early (or the length lied): try to fetch the rest.
                return new Step(StepKind.Retry, received, total, resume, "The download ended early.");
            }

            return new Step(StepKind.Completed, received, total, resume);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static ResumeInfo? ReadResumeInfo(HttpResponseMessage response)
    {
        var acceptsRanges = response.Headers.AcceptRanges.Contains("bytes", StringComparer.OrdinalIgnoreCase)
                            || response.StatusCode == HttpStatusCode.PartialContent;
        var etag = response.Headers.ETag is { IsWeak: false } tag ? tag.ToString() : null;
        var lastModified = response.Content.Headers.LastModified?.ToString("R", CultureInfo.InvariantCulture);
        return acceptsRanges || etag is not null || lastModified is not null ? new ResumeInfo(acceptsRanges, etag, lastModified) : null;
    }

    private static long PartialLength(string partialPath)
    {
        try
        {
            return File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The next full download overwrites it anyway (FileMode.Create).
        }
    }
}
