using System.Windows.Threading;
using AJBrowser.Core.Logging;
using AJBrowser.Core.Persistence;

namespace AJBrowser.Services;

/// <summary>
/// Saves the open tabs so "Continue where you left off" can bring them back. Changes are saved at most once a
/// second (a debounce: many quick changes, one write), and once more synchronously when the app exits.
/// </summary>
public sealed class SessionService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1);

    private readonly SessionRepository _repository;
    private readonly AppLogger _log;
    private readonly DispatcherTimer _timer;
    private Func<IReadOnlyList<SessionTab>>? _snapshot;
    private Task? _inFlight;
    private bool _stopped;

    public SessionService(SessionRepository repository, AppLogger log)
    {
        _repository = repository;
        _log = log;
        _timer = new DispatcherTimer { Interval = Debounce };
        _timer.Tick += OnTimerTick;
    }

    public async Task<IReadOnlyList<SessionTab>> LoadAsync()
    {
        try
        {
            return await _repository.LoadAsync();
        }
        catch (Exception ex)
        {
            _log.Error("Could not load the saved session", ex);
            return [];
        }
    }

    /// <summary>Asks for a save soon. <paramref name="snapshot"/> is called on the UI thread when the save happens.</summary>
    public void ScheduleSave(Func<IReadOnlyList<SessionTab>> snapshot)
    {
        if (_stopped)
        {
            return;
        }

        _snapshot = snapshot;
        _timer.Stop();
        _timer.Start(); // restart the countdown
    }

    /// <summary>Final save at exit (blocks briefly). Later changes (tabs disposed while closing) are ignored.</summary>
    public void SaveNowAndStop(IReadOnlyList<SessionTab> tabs)
    {
        _stopped = true;
        _timer.Stop();
        try
        {
            // Let a debounced save that's already running finish first, so it can't overwrite this final one.
            _inFlight?.Wait(TimeSpan.FromSeconds(2));
            _repository.Save(tabs);
            _log.Info($"Session saved ({tabs.Count} tabs)");
        }
        catch (Exception ex)
        {
            _log.Error("Could not save the session at exit", ex);
        }
    }

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        // async void: a timer event handler; everything is caught.
        _timer.Stop();
        if (_snapshot is not { } snapshot || _stopped)
        {
            return;
        }

        try
        {
            _inFlight = _repository.SaveAsync(snapshot());
            await _inFlight;
        }
        catch (Exception ex)
        {
            _log.Warn("Could not save the session", ex);
        }
    }
}
