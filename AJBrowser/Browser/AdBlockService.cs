using System.Windows.Threading;
using AJBrowser.Core.Blocking;
using AJBrowser.Core.Logging;
using AJBrowser.Core.Settings;
using Microsoft.Web.WebView2.Core;

namespace AJBrowser.Browser;

/// <summary>
/// Which ad blocker runs and whether it's on (Phase 8c). Two engines, never both at once:
/// <list type="bullet">
/// <item><b>uBlock Origin Lite</b> (default, Phase 2.5): an extension in the WebView2 profile, see <see cref="ExtensionManager"/>.</item>
/// <item><b>AJBrowser's own engine</b> (Phase 8): adblock-rust with uBO's filter lists (<see cref="FilterEngineService"/>),
/// applied per tab by <see cref="RequestBlocker"/> (network requests) and <see cref="CosmeticInjector"/> (scriptlets
/// and element hiding). Sites where the user turned it off are in <see cref="Policy"/>.</item>
/// </list>
/// The global switch and the engine choice are settings (<see cref="AppSettings.AdBlockEnabled"/>,
/// <see cref="AppSettings.AdBlockEngine"/>). UI thread only, except where noted; never throws to callers.
/// </summary>
public sealed class AdBlockService : IDisposable
{
    /// <summary>How long the first page waits for the saved engine to load (normally ~50 ms) before loading anyway.</summary>
    private static readonly TimeSpan FirstPageWaitLimit = TimeSpan.FromSeconds(3);

    /// <summary>First background list check after the engine started (so startup isn't slowed).</summary>
    private static readonly TimeSpan FirstUpdateDelay = TimeSpan.FromSeconds(30);

    /// <summary>How often lists are checked; each list is only downloaded when its own expiry has passed.</summary>
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(1);

    private readonly SettingsStore _settings;
    private readonly Func<FilterEngineService> _createFilters;
    private readonly AppLogger _log;
    private readonly Dispatcher _dispatcher;
    private readonly Task _policyLoaded;
    private readonly List<RequestBlocker> _blockers = [];

    private RequestBlocker? _workerOwner;
    private FilterEngineService? _filters;
    private Task? _ownEngineStart;
    private CancellationTokenSource? _updates;
    private string? _ownEngineProblem;
    private bool _downloadingLists;
    private bool _disposed;

    /// <param name="createFilters">Creates the list service (reads the 0.5 MB scriptlet file); called on a worker thread,
    /// only once AJBrowser's own engine is first needed.</param>
    public AdBlockService(
        SettingsStore settings,
        BlockingPolicy policy,
        Func<Func<bool>, ExtensionManager> createExtensions,
        Func<FilterEngineService> createFilters,
        bool savedExtensionChoice,
        AppLogger log)
    {
        _settings = settings;
        _createFilters = createFilters;
        _log = log;
        _dispatcher = Dispatcher.CurrentDispatcher;
        Policy = policy;

        // Settings from before Phase 8c have no switch: take the choice uBO Lite's state file remembered.
        if (settings.Current.AdBlockEnabled is null)
        {
            settings.Update(s => s with { AdBlockEnabled = savedExtensionChoice });
        }

        Policy.Enabled = Enabled;
        Extensions = createExtensions(() => Enabled && Engine == AdBlockEngineKind.UBlockOriginLite);
        Extensions.StateChanged += OnExtensionStateChanged;
        Policy.Changed += OnPolicyChanged;
        _policyLoaded = LoadPolicyAsync();
    }

    /// <summary>The bundled uBlock Origin Lite.</summary>
    public ExtensionManager Extensions { get; }

    /// <summary>Global switch + sites where AJBrowser's engine is off.</summary>
    public BlockingPolicy Policy { get; }

    public AdBlockEngineKind Engine => _settings.Current.AdBlockEngine;

    /// <summary>The global "Block ads" switch.</summary>
    public bool Enabled => _settings.Current.AdBlockEnabled ?? true;

    public bool UsesOwnEngine => Engine == AdBlockEngineKind.AJBrowser;

    /// <summary>Null when AJBrowser's engine can run on this PC; otherwise why not (the DLL is missing…).</summary>
    public static string? OwnEngineUnavailableReason => AdblockRustEngine.UnavailableReason;

    /// <summary>
    /// The compiled engine tabs should use right now, or null (switch off, uBO Lite chosen, lists not downloaded yet).
    /// Safe from any thread.
    /// </summary>
    public IFilterEngine? ActiveEngine => UsesOwnEngine && Enabled ? Volatile.Read(ref _filters)?.Engine : null;

    /// <summary>Lists are being downloaded for the first time (blocking starts when they're ready).</summary>
    public bool IsDownloadingLists => _downloadingLists;

    /// <summary>The chosen engine can block (installed / loaded).</summary>
    public bool IsAvailable => UsesOwnEngine ? OwnEngineUnavailableReason is null : Extensions.IsAvailable;

    /// <summary>Ads are actually being blocked by the chosen engine (or will be once its lists arrive).</summary>
    public bool IsBlocking => Enabled && (UsesOwnEngine ? OwnEngineUnavailableReason is null : Extensions.IsEnabled);

    /// <summary>One line about AJBrowser's own engine for Settings (rules, downloading, problem); null when it isn't chosen.</summary>
    public string? OwnEngineStatus
    {
        get
        {
            if (!UsesOwnEngine)
            {
                return null;
            }

            if (OwnEngineUnavailableReason is { } reason)
            {
                return reason;
            }

            if (_downloadingLists)
            {
                return "Downloading filter lists (about 5 MB)…";
            }

            if (_ownEngineProblem is { } problem)
            {
                return problem;
            }

            var filters = Volatile.Read(ref _filters);
            if (filters is null || !Enabled)
            {
                return Enabled ? "Starting…" : "Off";
            }

            var status = filters.GetStatus().Where(s => s.Enabled).ToArray();
            var rules = status.Sum(s => s.Rules);
            var failed = status.Count(s => s.Error is not null);
            var newest = status.Max(s => s.UpdatedAt);
            var text = filters.Engine is null
                ? "No filter lists yet: they download when a connection is available."
                : $"{status.Length} filter lists, {rules:N0} rules";
            if (newest is { } updated && filters.Engine is not null)
            {
                text += $", updated {updated.ToLocalTime():d MMM, HH:mm}";
            }

            if (failed > 0)
            {
                text += $" · {failed} list(s) couldn't be updated (kept the last copy)";
            }

            return text;
        }
    }

    /// <summary>Raised on the UI thread when anything above changed.</summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Raised on the UI thread when what tabs must do changed: the engine was swapped, the switch or engine choice
    /// changed, or a site was turned on/off. Tabs then re-check their page and drop injected scripts.
    /// </summary>
    public event EventHandler? BlockingChanged;

    /// <summary>
    /// Called by every new WebView before its first navigation. uBO Lite: its install (first WebViews only).
    /// AJBrowser's engine: the saved engine and the per-site settings (first pages wait up to 3 s). Never throws.
    /// </summary>
    public async Task PrepareAsync(CoreWebView2 core)
    {
        var extension = Extensions.PrepareAsync(core);
        if (!UsesOwnEngine || !Enabled)
        {
            await extension;
            return;
        }

        var ready = Task.WhenAll(extension, _policyLoaded, EnsureOwnEngineStarted());
        await Task.WhenAny(ready, Task.Delay(FirstPageWaitLimit));
    }

    /// <summary>Called once at startup, after the window shows: starts AJBrowser's engine if it's the chosen one.</summary>
    public void Start()
    {
        if (UsesOwnEngine && Enabled)
        {
            _ = StartOwnEngineAndUpdatesAsync();
        }
    }

    /// <summary>The global switch.</summary>
    public async Task SetEnabledAsync(bool enabled)
    {
        if (enabled == Enabled)
        {
            return;
        }

        _settings.Update(s => s with { AdBlockEnabled = enabled });
        _log.Info($"Ad blocking turned {(enabled ? "on" : "off")} ({EngineName(Engine)})");
        await ApplyAsync();
    }

    /// <summary>Switches engines: uBO Lite is disabled in the profile while AJBrowser's engine runs, and the other way round.</summary>
    public async Task SetEngineAsync(AdBlockEngineKind engine)
    {
        if (engine == Engine)
        {
            return;
        }

        _settings.Update(s => s with { AdBlockEngine = engine });
        _log.Info($"Ad-block engine switched to {EngineName(engine)}");
        await ApplyAsync();
    }

    /// <summary>"Update now" for AJBrowser's lists (8d's Settings button; also used for checks). Never throws.</summary>
    public async Task UpdateListsNowAsync()
    {
        if (!UsesOwnEngine || await GetFiltersAsync() is not { } filters)
        {
            return;
        }

        try
        {
            var summary = await Task.Run(() => filters.UpdateAsync(force: true));
            _log.Info($"Filter lists checked: {summary.Checked}, changed {summary.Changed}, failed {summary.Failed}");
        }
        catch (Exception ex)
        {
            _log.Error("Updating the filter lists failed", ex);
        }

        RaiseStateChanged();
    }

    public static string EngineName(AdBlockEngineKind engine) =>
        engine == AdBlockEngineKind.AJBrowser ? "AJBrowser's engine" : ExtensionManager.ExtensionName;

    // ---------------------------------------------------------------- tabs

    /// <summary>A tab's blocker attached to a WebView. One of them also filters service/shared worker requests,
    /// which belong to no tab (spike F7: register those source kinds on one WebView only).</summary>
    internal void Register(RequestBlocker blocker)
    {
        _blockers.Add(blocker);
        if (_workerOwner is null)
        {
            _workerOwner = blocker;
            blocker.SetWorkerOwner(true);
        }
    }

    internal void Unregister(RequestBlocker blocker)
    {
        _blockers.Remove(blocker);
        if (ReferenceEquals(_workerOwner, blocker))
        {
            blocker.SetWorkerOwner(false);
            _workerOwner = _blockers.FirstOrDefault();
            _workerOwner?.SetWorkerOwner(true);
        }
    }

    /// <summary>Should AJBrowser's engine act on this page (the tab's top-level address)? Returns the engine, or null.</summary>
    internal IFilterEngine? EngineFor(string? pageUrl) =>
        ActiveEngine is { } engine && Policy.IsEnabledFor(pageUrl) ? engine : null;

    // ---------------------------------------------------------------- internals

    private async Task ApplyAsync()
    {
        Policy.Enabled = Enabled;
        await Extensions.ApplyAsync();
        if (UsesOwnEngine && Enabled)
        {
            await StartOwnEngineAndUpdatesAsync();
        }
        else
        {
            StopUpdates();
        }

        RaiseStateChanged();
        BlockingChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task StartOwnEngineAndUpdatesAsync()
    {
        await EnsureOwnEngineStarted();
        if (_disposed || !UsesOwnEngine || !Enabled || Volatile.Read(ref _filters) is not { } filters || _updates is not null)
        {
            return;
        }

        var cts = _updates = new CancellationTokenSource();
        if (filters.Engine is null)
        {
            // First use (or every list missing): download now; pages are unfiltered until this finishes (~3 s).
            _downloadingLists = true;
            RaiseStateChanged();
            try
            {
                var summary = await Task.Run(() => filters.UpdateAsync(force: false, cts.Token));
                _log.Info($"Filter lists downloaded: {summary.Changed} of {summary.Checked}, failed {summary.Failed}");
                _ownEngineProblem = filters.Engine is null
                    ? "The filter lists couldn't be downloaded. AJBrowser tries again every hour."
                    : null;
            }
            catch (OperationCanceledException)
            {
                // switched away meanwhile
            }
            catch (Exception ex)
            {
                _log.Error("Downloading the filter lists failed", ex);
                _ownEngineProblem = "The filter lists couldn't be downloaded. AJBrowser tries again every hour.";
            }
            finally
            {
                _downloadingLists = false;
                RaiseStateChanged();
            }
        }

        if (cts.IsCancellationRequested)
        {
            return;
        }

        var delay = filters.Engine is null ? UpdateInterval : FirstUpdateDelay;
        _ = Task.Run(async () =>
        {
            try
            {
                await filters.RunAsync(delay, UpdateInterval, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // stopped
            }
        });
    }

    private void StopUpdates()
    {
        _updates?.Cancel();
        _updates?.Dispose();
        _updates = null;
    }

    private Task EnsureOwnEngineStarted() => _ownEngineStart ??= StartOwnEngineAsync();

    private async Task<FilterEngineService?> GetFiltersAsync()
    {
        await EnsureOwnEngineStarted();
        return Volatile.Read(ref _filters);
    }

    /// <summary>Creates the list service and loads the saved engine (no network). Runs once. Never throws.</summary>
    private async Task StartOwnEngineAsync()
    {
        if (OwnEngineUnavailableReason is { } reason)
        {
            _log.Warn($"AJBrowser's ad-block engine can't run: {reason}");
            return;
        }

        try
        {
            var lists = _settings.Current.EnabledFilterLists;
            var filters = await Task.Run(async () =>
            {
                var created = _createFilters();
                await created.InitializeAsync(lists).ConfigureAwait(false);
                return created;
            });
            filters.EngineChanged += OnFilterEngineChanged;
            filters.StatusChanged += OnFilterStatusChanged;
            Volatile.Write(ref _filters, filters);
            _log.Info(filters.Engine is null
                ? "AJBrowser's ad-block engine started without lists (they download next)"
                : $"AJBrowser's ad-block engine loaded ({filters.GetStatus().Where(s => s.Enabled).Sum(s => s.Rules):N0} rules)");
            OnFilterEngineChanged(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _ownEngineProblem = "AJBrowser's engine couldn't start (details in the log).";
            _log.Error("AJBrowser's ad-block engine couldn't start", ex);
            RaiseStateChanged();
        }
    }

    private async Task LoadPolicyAsync()
    {
        try
        {
            await Task.Run(Policy.LoadAsync);
        }
        catch (Exception ex)
        {
            _log.Error("Sites with ad blocking turned off couldn't be loaded", ex);
        }
    }

    /// <summary>Raised by the list service on a worker thread after the engine was swapped.</summary>
    private void OnFilterEngineChanged(object? sender, EventArgs e)
    {
        // The first cosmetic lookup costs ~50 ms and the first generic lookup ~10 ms (F25): pay that here, not in a page.
        if (Volatile.Read(ref _filters)?.Engine is { } engine)
        {
            _ = Task.Run(() =>
            {
                engine.GetCosmeticResources("https://www.example.com/");
                engine.GetHiddenClassIdSelectors(["ad"], ["ad"], []);
            });
        }

        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            RaiseStateChanged();
            BlockingChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private void OnFilterStatusChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(RaiseStateChanged);

    private void OnPolicyChanged(object? sender, EventArgs e)
    {
        if (_dispatcher.CheckAccess())
        {
            BlockingChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _dispatcher.BeginInvoke(() => BlockingChanged?.Invoke(this, EventArgs.Empty));
        }
    }

    private void OnExtensionStateChanged(object? sender, EventArgs e) => RaiseStateChanged();

    private void RaiseStateChanged()
    {
        if (!_disposed)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopUpdates();
        Extensions.StateChanged -= OnExtensionStateChanged;
        Policy.Changed -= OnPolicyChanged;
        if (Volatile.Read(ref _filters) is { } filters)
        {
            filters.EngineChanged -= OnFilterEngineChanged;
            filters.StatusChanged -= OnFilterStatusChanged;
            filters.Dispose();
        }
    }
}
