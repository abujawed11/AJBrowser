using AJBrowser.Core.Persistence;

namespace AJBrowser.Core.Downloads;

/// <summary>
/// Owns every download, browser-managed and app-managed, and keeps the database in step (plan Phase 4).
/// <list type="bullet">
/// <item><b>App downloads</b> run here with <see cref="HttpFileDownloader"/>, at most <see cref="MaxConcurrent"/> at
/// a time (the rest wait as Queued). Pause keeps the partial file; Cancel deletes it.</item>
/// <item><b>Browser downloads</b> run in the WebView2 runtime; the Browser layer reports their progress with
/// <see cref="UpdateBrowserDownload"/> (Phase 4b). The runtime decides how many run at once.</item>
/// <item>Progress events are throttled (about 4 per second per download) and progress is saved every 2 s; state
/// changes are raised and saved immediately. Speed and time left are smoothed by <see cref="TransferRateEstimator"/>.</item>
/// </list>
/// Events are raised on the <see cref="SynchronizationContext"/> the manager was created on (the UI thread in the
/// app), so views can update directly. Methods may be called from any thread.
/// </summary>
public sealed class DownloadManager
{
    public const int DefaultMaxConcurrent = 3;

    private static readonly TimeSpan ProgressEventInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ProgressSaveInterval = TimeSpan.FromSeconds(2);

    private readonly DownloadRepository _repository;
    private readonly HttpFileDownloader _downloader;
    private readonly IStreamDownloader? _streams;
    private readonly FileNamer _namer;
    private readonly Func<DateTime> _utcNow;
    private readonly SynchronizationContext? _context;
    private readonly object _lock = new();
    private readonly Dictionary<long, Entry> _entries = [];
    private readonly List<Task> _running = [];
    private Task _writes = Task.CompletedTask;
    private int _maxConcurrent = DefaultMaxConcurrent;
    private bool _shuttingDown;

    /// <param name="streams">Runs HLS and DASH jobs (Phases 6–7); without it they fail with a clear message.</param>
    public DownloadManager(DownloadRepository repository, HttpFileDownloader downloader, FileNamer namer, Func<DateTime>? utcNow = null, IStreamDownloader? streams = null)
    {
        _repository = repository;
        _downloader = downloader;
        _streams = streams;
        _namer = namer;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _context = SynchronizationContext.Current;
    }

    /// <summary>A download was added or changed (the snapshot is the new state).</summary>
    public event EventHandler<DownloadItem>? ItemChanged;

    /// <summary>A download was removed from the list (its file, if any, is untouched).</summary>
    public event EventHandler<long>? ItemRemoved;

    /// <summary>Saving to the database failed (the app logs it; the download itself continues).</summary>
    public event EventHandler<Exception>? PersistenceFailed;

    /// <summary>How many app downloads may run at once (1–10).</summary>
    public int MaxConcurrent
    {
        get => _maxConcurrent;
        set
        {
            _maxConcurrent = Math.Clamp(value, 1, 10);
            Pump();
        }
    }

    /// <summary>All downloads, newest first.</summary>
    public IReadOnlyList<DownloadItem> Items
    {
        get
        {
            lock (_lock)
            {
                return _entries.Values.Select(e => e.Item).OrderByDescending(i => i.StartedAtUtc).ThenByDescending(i => i.Id).ToList();
            }
        }
    }

    public DownloadItem? Find(long id)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(id, out var entry) ? entry.Item : null;
        }
    }

    /// <summary>Loads the recent list. Downloads that were running when the app closed are marked first (see <see cref="DownloadRepository.MarkStaleAsync"/>).</summary>
    public async Task InitializeAsync(int limit = 500)
    {
        await _repository.MarkStaleAsync().ConfigureAwait(false);
        var items = await _repository.GetRecentAsync(limit).ConfigureAwait(false);
        lock (_lock)
        {
            foreach (var item in items)
            {
                _entries[item.Id] = new Entry(item);
            }
        }
    }

    // ---------------------------------------------------------------- app-managed downloads

    /// <summary>
    /// Adds an app download and starts it when a slot is free. <see cref="DownloadRequest.TargetPath"/> should come from
    /// <see cref="FileNamer.ReservePath"/>; the reservation is released when the download ends.
    /// Request headers are kept in memory only (a retry after a restart goes without them).
    /// </summary>
    public async Task<DownloadItem> EnqueueAsync(DownloadRequest request, string? pageUrl = null)
    {
        var item = await _repository.AddAsync(new DownloadItem
        {
            Kind = DownloadKind.App,
            Source = request.Source,
            AudioSourceUrl = request.AudioUrl?.AbsoluteUri,
            VideoTrackId = request.VideoTrackId,
            AudioTrackId = request.AudioTrackId,
            TotalBytes = request.ExpectedBytes is > 0 ? request.ExpectedBytes : null,
            State = DownloadState.Queued,
            SourceUrl = request.Url.AbsoluteUri,
            PageUrl = pageUrl,
            FilePath = request.TargetPath,
            StartedAtUtc = _utcNow(),
        }).ConfigureAwait(false);

        lock (_lock)
        {
            _entries[item.Id] = new Entry(item) { Headers = request.Headers };
        }

        Raise(item);
        Pump();
        return item;
    }

    /// <summary>Pauses an app download, keeping its partial file. Queued ones simply wait as Paused.</summary>
    public void Pause(long id)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.Item.Kind != DownloadKind.App)
            {
                return;
            }

            if (entry.Cts is { } running)
            {
                entry.StopReason = StopReason.Pause;
                running.Cancel();
                return;
            }

            if (entry.Item.State == DownloadState.Queued)
            {
                SetLocked(entry, entry.Item with { State = DownloadState.Paused });
            }
        }
    }

    /// <summary>Continues a paused or interrupted app download (from its partial file when the server allows).</summary>
    public void Resume(long id)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(id, out var entry) && entry.Item.Kind == DownloadKind.App
                && entry.Item.State is DownloadState.Paused or DownloadState.Interrupted && entry.Cts is null)
            {
                SetLocked(entry, entry.Item with { State = DownloadState.Queued, Error = null });
            }
        }

        Pump();
    }

    /// <summary>
    /// Gives a stopped app download a fresh address and request headers (plan Phase 5: signed video links expire, and
    /// replaying the video gives a new one for the same file). Only for app downloads that aren't running; the partial
    /// file is kept and resumed only if the server confirms it's the same file (ETag / Last-Modified). Returns true if done.
    /// </summary>
    public bool ReplaceSource(long id, Uri url, IReadOnlyDictionary<string, string>? headers)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.Item.Kind != DownloadKind.App || entry.Cts is not null
                || entry.Item.State is DownloadState.Downloading or DownloadState.Queued or DownloadState.Completed)
            {
                return false;
            }

            entry.Headers = headers;
            entry.Item = entry.Item with { SourceUrl = url.AbsoluteUri };
            Save(entry.Item);
            Raise(entry.Item);
            return true;
        }
    }

    /// <summary>Starts a failed, cancelled or interrupted app download again.</summary>
    public void Retry(long id)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(id, out var entry) && entry.Item.Kind == DownloadKind.App && entry.Item.CanRetry && entry.Cts is null)
            {
                SetLocked(entry, entry.Item with { State = DownloadState.Queued, Error = null, CompletedAtUtc = null });
            }
        }

        Pump();
    }

    /// <summary>Cancels an app download and deletes its partial file.</summary>
    public void Cancel(long id)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.Item.Kind != DownloadKind.App || !entry.Item.CanCancel)
            {
                return;
            }

            if (entry.Cts is { } running)
            {
                entry.StopReason = StopReason.Cancel;
                running.Cancel();
                return;
            }

            DeletePartialLocked(entry.Item);
            _namer.Release(entry.Item.FilePath);
            SetLocked(entry, entry.Item with { State = DownloadState.Cancelled, ReceivedBytes = 0, BytesPerSecond = null, Remaining = null });
        }
    }

    // ---------------------------------------------------------------- browser-managed downloads (reported by the Browser layer)

    /// <summary>Registers a download the WebView2 runtime just started.</summary>
    public async Task<DownloadItem> AddBrowserDownloadAsync(string sourceUrl, string? pageUrl, string filePath, long? totalBytes)
    {
        var item = await _repository.AddAsync(new DownloadItem
        {
            Kind = DownloadKind.Browser,
            State = DownloadState.Downloading,
            SourceUrl = sourceUrl,
            PageUrl = pageUrl,
            FilePath = filePath,
            TotalBytes = totalBytes,
            StartedAtUtc = _utcNow(),
        }).ConfigureAwait(false);

        lock (_lock)
        {
            _entries[item.Id] = new Entry(item);
        }

        Raise(item);
        return item;
    }

    /// <summary>The runtime reported progress or a state change for a browser download.</summary>
    public void UpdateBrowserDownload(long id, DownloadState state, long receivedBytes, long? totalBytes, bool canResume, string? error = null, string? filePath = null)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.Item.Kind != DownloadKind.Browser)
            {
                return;
            }

            if (!DownloadStateMachine.CanMove(entry.Item.State, state))
            {
                return; // late or out-of-order report
            }

            var now = _utcNow();
            var stateChanged = state != entry.Item.State;
            entry.Rate.Add(now, receivedBytes);
            var running = state == DownloadState.Downloading;
            var updated = entry.Item with
            {
                State = state,
                ReceivedBytes = receivedBytes,
                TotalBytes = totalBytes ?? entry.Item.TotalBytes,
                BrowserCanResume = canResume,
                Error = state is DownloadState.Failed or DownloadState.Interrupted ? error ?? entry.Item.Error : null,
                FilePath = filePath ?? entry.Item.FilePath,
                CompletedAtUtc = state == DownloadState.Completed ? now : entry.Item.CompletedAtUtc,
                BytesPerSecond = running ? entry.Rate.BytesPerSecond : null,
                Remaining = running ? entry.Rate.Remaining(totalBytes ?? entry.Item.TotalBytes) : null,
            };

            if (stateChanged)
            {
                if (!running)
                {
                    entry.Rate.Reset();
                }

                if (updated.IsFinished)
                {
                    _namer.Release(updated.FilePath);
                }

                SetLocked(entry, updated);
            }
            else
            {
                ProgressLocked(entry, updated, now);
            }
        }
    }

    // ---------------------------------------------------------------- list maintenance

    /// <summary>Removes a finished or paused download from the list (not the file). Running ones are left alone.</summary>
    public async Task RemoveAsync(long id)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.Cts is not null || entry.Item.State == DownloadState.Downloading)
            {
                return;
            }

            _entries.Remove(id);
            _namer.Release(entry.Item.FilePath);
        }

        await _repository.DeleteAsync(id).ConfigureAwait(false);
        RaiseRemoved(id);
    }

    /// <summary>Removes every completed, failed, cancelled or interrupted download from the list. Returns how many.</summary>
    public async Task<int> ClearFinishedAsync()
    {
        List<long> removed;
        lock (_lock)
        {
            removed = _entries.Values.Where(e => e.Item.IsFinished && e.Cts is null).Select(e => e.Item.Id).ToList();
            foreach (var id in removed)
            {
                _entries.Remove(id);
            }
        }

        await _repository.DeleteFinishedAsync().ConfigureAwait(false);
        foreach (var id in removed)
        {
            RaiseRemoved(id);
        }

        return removed.Count;
    }

    /// <summary>
    /// App exit: pauses running app downloads (keeping partial files, so they resume next time), then waits (up to
    /// <paramref name="timeout"/>) for them to stop and for pending saves to finish.
    /// </summary>
    public async Task ShutdownAsync(TimeSpan timeout)
    {
        Task[] running;
        lock (_lock)
        {
            _shuttingDown = true;
            foreach (var entry in _entries.Values.Where(e => e.Cts is not null))
            {
                entry.StopReason = StopReason.Pause;
                entry.Cts!.Cancel();
            }

            running = [.. _running];
        }

        var all = Task.WhenAll(running.Append(WritesSnapshot()));
        await Task.WhenAny(all, Task.Delay(timeout)).ConfigureAwait(false);
        await Task.WhenAny(WritesSnapshot(), Task.Delay(timeout)).ConfigureAwait(false);
    }

    /// <summary>For tests: waits until no app download is running and all saves are written.</summary>
    internal async Task WhenIdleAsync()
    {
        while (true)
        {
            Task[] running;
            lock (_lock)
            {
                running = [.. _running];
            }

            if (running.Length == 0)
            {
                break;
            }

            await Task.WhenAll(running).ConfigureAwait(false);
        }

        await WritesSnapshot().ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- running app downloads

    /// <summary>Starts queued app downloads while there are free slots (oldest first).</summary>
    private void Pump()
    {
        lock (_lock)
        {
            if (_shuttingDown)
            {
                return;
            }

            var runningCount = _entries.Values.Count(e => e.Cts is not null);
            foreach (var entry in _entries.Values
                         .Where(e => e.Item.Kind == DownloadKind.App && e.Item.State == DownloadState.Queued && e.Cts is null)
                         .OrderBy(e => e.Item.StartedAtUtc).ThenBy(e => e.Item.Id)
                         .Take(Math.Max(0, _maxConcurrent - runningCount))
                         .ToList())
            {
                entry.Cts = new CancellationTokenSource();
                entry.StopReason = StopReason.None;
                entry.Rate.Reset();
                SetLocked(entry, entry.Item with { State = DownloadState.Downloading, Error = null });

                Task task = null!;
                task = Task.Run(async () =>
                {
                    try
                    {
                        await RunAsync(entry).ConfigureAwait(false);
                    }
                    finally
                    {
                        lock (_lock)
                        {
                            _running.Remove(task);
                        }

                        Pump();
                    }
                });
                _running.Add(task);
            }
        }
    }

    private async Task RunAsync(Entry entry)
    {
        DownloadItem start;
        CancellationToken token;
        lock (_lock)
        {
            start = entry.Item;
            token = entry.Cts!.Token;
        }

        var progress = new InlineProgress(p => OnAppProgress(entry, p));
        DownloadResult result;
        try
        {
            var audio = Uri.TryCreate(start.AudioSourceUrl, UriKind.Absolute, out var audioUri) ? audioUri : null;
            var request = new DownloadRequest(new Uri(start.SourceUrl), start.FilePath, entry.Headers, start.Source, audio, start.VideoTrackId, start.AudioTrackId, start.TotalBytes);
            result = start.Source switch
            {
                DownloadSource.File => await _downloader.DownloadAsync(request, start.Resume, progress, token).ConfigureAwait(false),
                _ when _streams is not null => await _streams.DownloadAsync(start.Id, request, progress, token).ConfigureAwait(false),
                _ => new DownloadResult(DownloadOutcome.Failed, 0, null, null, "Stream downloads aren't available in this version."),
            };
        }
        catch (Exception ex)
        {
            // The downloader handles network and file errors itself; anything here is unexpected.
            result = new DownloadResult(DownloadOutcome.Failed, start.ReceivedBytes, start.TotalBytes, start.Resume, $"Unexpected error: {ex.GetType().Name}");
        }

        lock (_lock)
        {
            var reason = entry.StopReason;
            entry.Cts!.Dispose();
            entry.Cts = null;
            entry.Rate.Reset();

            var item = entry.Item with { Resume = result.Resume ?? entry.Item.Resume, TotalBytes = result.TotalBytes ?? entry.Item.TotalBytes, BytesPerSecond = null, Remaining = null };
            switch (result.Outcome)
            {
                case DownloadOutcome.Completed:
                    _namer.Release(item.FilePath);
                    SetLocked(entry, item with
                    {
                        State = DownloadState.Completed,
                        ReceivedBytes = result.ReceivedBytes,
                        TotalBytes = item.Source == DownloadSource.File ? item.TotalBytes : result.ReceivedBytes, // streams: real size, not the estimate
                        FilePath = result.FinalPath ?? item.FilePath,
                        CompletedAtUtc = _utcNow(),
                    });
                    break;

                case DownloadOutcome.Cancelled when reason == StopReason.Cancel:
                    DeletePartialLocked(item);
                    _namer.Release(item.FilePath);
                    SetLocked(entry, item with { State = DownloadState.Cancelled, ReceivedBytes = 0 });
                    break;

                case DownloadOutcome.Cancelled:
                    SetLocked(entry, item with { State = DownloadState.Paused, ReceivedBytes = result.ReceivedBytes });
                    break;

                default:
                    SetLocked(entry, item with { State = DownloadState.Failed, ReceivedBytes = result.ReceivedBytes, Error = result.Error });
                    break;
            }
        }
    }

    private void OnAppProgress(Entry entry, DownloadProgress progress)
    {
        lock (_lock)
        {
            if (entry.Item.State != DownloadState.Downloading)
            {
                return;
            }

            var now = _utcNow();
            entry.Rate.Add(now, progress.ReceivedBytes);
            var total = progress.TotalBytes ?? entry.Item.TotalBytes;
            ProgressLocked(entry, entry.Item with
            {
                ReceivedBytes = progress.ReceivedBytes,
                TotalBytes = total,
                BytesPerSecond = entry.Rate.BytesPerSecond,
                Remaining = entry.Rate.Remaining(total),
            }, now);
        }
    }

    /// <summary>Cancel: removes a file download's .ajpart, or a stream's temporary pieces.</summary>
    private void DeletePartialLocked(DownloadItem item)
    {
        HttpFileDownloader.DeletePartial(item.FilePath);
        if (item.Source != DownloadSource.File)
        {
            _streams?.DeleteTemp(item.Id);
        }
    }

    // ---------------------------------------------------------------- state, events, saving

    /// <summary>A state change: always raised and saved. Call with the lock held.</summary>
    private void SetLocked(Entry entry, DownloadItem updated)
    {
        if (!DownloadStateMachine.CanMove(entry.Item.State, updated.State))
        {
            return;
        }

        var now = _utcNow();
        entry.Item = updated;
        entry.LastEvent = now;
        entry.LastSave = now;
        Raise(updated);
        Save(updated);
    }

    /// <summary>Progress: raised at most every 250 ms, saved at most every 2 s. Call with the lock held.</summary>
    private void ProgressLocked(Entry entry, DownloadItem updated, DateTime now)
    {
        entry.Item = updated;
        if (now - entry.LastEvent >= ProgressEventInterval)
        {
            entry.LastEvent = now;
            Raise(updated);
        }

        if (now - entry.LastSave >= ProgressSaveInterval)
        {
            entry.LastSave = now;
            Save(updated);
        }
    }

    /// <summary>Saves run one after another (never two writes for the same row out of order).</summary>
    private void Save(DownloadItem item)
    {
        lock (_lock)
        {
            _writes = _writes.ContinueWith(async _ =>
            {
                try
                {
                    await _repository.UpdateAsync(item).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    PersistenceFailed?.Invoke(this, ex);
                }
            }, TaskScheduler.Default).Unwrap();
        }
    }

    private Task WritesSnapshot()
    {
        lock (_lock)
        {
            return _writes;
        }
    }

    private void Raise(DownloadItem item)
    {
        if (_context is null)
        {
            ItemChanged?.Invoke(this, item);
        }
        else
        {
            _context.Post(_ => ItemChanged?.Invoke(this, item), null);
        }
    }

    private void RaiseRemoved(long id)
    {
        if (_context is null)
        {
            ItemRemoved?.Invoke(this, id);
        }
        else
        {
            _context.Post(_ => ItemRemoved?.Invoke(this, id), null);
        }
    }

    private enum StopReason
    {
        None,
        Pause,
        Cancel,
    }

    private sealed class Entry(DownloadItem item)
    {
        public DownloadItem Item { get; set; } = item;
        public TransferRateEstimator Rate { get; } = new();
        public CancellationTokenSource? Cts { get; set; }
        public StopReason StopReason { get; set; }
        public IReadOnlyDictionary<string, string>? Headers { get; set; }
        public DateTime LastEvent { get; set; }
        public DateTime LastSave { get; set; }
    }

    /// <summary>Reports progress synchronously on the downloader's thread (Progress&lt;T&gt; would post and reorder).</summary>
    private sealed class InlineProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }
}
