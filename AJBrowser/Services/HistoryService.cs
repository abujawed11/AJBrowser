using AJBrowser.Core.Logging;
using AJBrowser.Core.Navigation;
using AJBrowser.Core.Persistence;

namespace AJBrowser.Services;

/// <summary>
/// The app's view of browsing history. Tabs call <see cref="RecordVisit"/> and <see cref="UpdateTitle"/> without
/// waiting; the writes run one after another on a background thread (so a title update can't overtake the visit
/// it belongs to). Failures are logged, never shown: losing one history entry is not worth an error dialog.
/// Call it from the UI thread.
/// </summary>
public sealed class HistoryService
{
    private readonly HistoryRepository _repository;
    private readonly AppLogger _log;
    private Task _writes = Task.CompletedTask;

    public HistoryService(HistoryRepository repository, AppLogger log)
    {
        _repository = repository;
        _log = log;
    }

    /// <summary>Entries were deleted (History page and suggestions should refresh).</summary>
    public event EventHandler? Cleared;

    /// <summary>Records a visit if the URL belongs in history (http/https, without #fragment).</summary>
    public void RecordVisit(string? url, string? title)
    {
        if (HistoryRules.UrlToRecord(url) is not { } recordUrl)
        {
            return;
        }

        var now = DateTime.UtcNow;
        QueueWrite(() => _repository.RecordVisitAsync(recordUrl, title, now), "record a visit");
    }

    public void UpdateTitle(string? url, string title)
    {
        if (HistoryRules.UrlToRecord(url) is { } recordUrl && !string.IsNullOrWhiteSpace(title))
        {
            QueueWrite(() => _repository.UpdateTitleAsync(recordUrl, title), "update a title");
        }
    }

    /// <summary>Waits for pending writes first, so a page just visited is included.</summary>
    public async Task<IReadOnlyList<HistoryEntry>> SearchAsync(string? query, int limit, int offset = 0)
    {
        await _writes;
        return await _repository.SearchAsync(query, limit, offset);
    }

    public Task<IReadOnlyList<HistoryEntry>> FindSuggestionCandidatesAsync(string text, int limit) =>
        _repository.FindSuggestionCandidatesAsync(text, limit);

    public async Task DeleteAsync(long id)
    {
        QueueWrite(() => _repository.DeleteAsync(id), "delete an entry");
        await _writes;
        Cleared?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Deletes entries visited since <paramref name="fromUtc"/> (null = everything). Returns how many.</summary>
    public async Task<int> ClearAsync(DateTime? fromUtc)
    {
        await _writes;
        var deleted = await _repository.DeleteRangeAsync(fromUtc ?? DateTime.UnixEpoch, DateTime.UtcNow.AddMinutes(1));
        _log.Info($"Cleared {deleted} history entries ({(fromUtc is null ? "all time" : "since " + fromUtc.Value.ToString("u"))})");
        Cleared?.Invoke(this, EventArgs.Empty);
        return deleted;
    }

    private void QueueWrite(Func<Task> work, string what)
    {
        _writes = _writes.ContinueWith(async _ =>
        {
            try
            {
                await work();
            }
            catch (Exception ex)
            {
                _log.Warn($"History: could not {what}", ex);
            }
        }, TaskScheduler.Default).Unwrap();
    }
}
