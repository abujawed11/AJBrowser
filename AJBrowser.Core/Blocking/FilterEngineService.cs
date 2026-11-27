using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AJBrowser.Core.Logging;

namespace AJBrowser.Core.Blocking;

/// <summary>What Settings shows for one list.</summary>
/// <param name="UpdatedAt">When its content last changed (or was first downloaded).</param>
/// <param name="CheckedAt">When AJBrowser last asked the server.</param>
/// <param name="Rules">Rule lines (comments and blanks not counted); 0 until downloaded.</param>
/// <param name="Error">The last download problem as a sentence; null when the last check worked.</param>
public sealed record FilterListStatus(
    FilterListInfo Info,
    bool Enabled,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? CheckedAt,
    int Rules,
    string? Error)
{
    public bool Downloaded => Rules > 0 || UpdatedAt is not null;
}

/// <summary>The outcome of one update round.</summary>
public sealed record FilterUpdateSummary(int Checked, int Changed, int Failed, bool Rebuilt);

/// <summary>
/// Owns AJBrowser's ad-block engine: keeps the enabled filter lists downloaded and fresh, compiles them into an
/// <see cref="IFilterEngine"/>, saves the compiled engine so the next start loads it in milliseconds, and swaps in a
/// new engine atomically whenever lists change.
/// <para>Files under the Filters folder: <c>lists\&lt;id&gt;.txt</c> (assembled list texts, the last good copy),
/// <c>lists\state.json</c>, <c>raw\</c> (download cache), <c>engine.dat</c> + <c>engine.json</c>.</para>
/// <para>All public methods may be called from any thread; slow work runs on the thread pool, one operation at a
/// time. <see cref="Engine"/> is read lock-free (the request filter reads it for every request).</para>
/// </summary>
public sealed class FilterEngineService : IDisposable
{
    public static readonly TimeSpan DefaultExpiry = TimeSpan.FromDays(4);
    public static readonly TimeSpan MinExpiry = TimeSpan.FromHours(1);
    public static readonly TimeSpan MaxExpiry = TimeSpan.FromDays(14);
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);

    private const int ParallelDownloads = 4;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly FilterListDownloader _downloader;
    private readonly string? _resourcesJson;
    private readonly AppLogger _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly string _listsFolder;
    private readonly string _stateFile;
    private readonly string _engineFile;
    private readonly string _engineMetaFile;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _stateLock = new();

    private Dictionary<string, ListState> _state = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> _enabled = FilterListCatalog.DefaultIds;
    private IFilterEngine? _engine;
    private string? _engineFingerprint;
    private bool _disposed;

    /// <param name="downloader">Downloads lists (its cache lives in Filters\raw).</param>
    /// <param name="filtersFolder">AppPaths.Filters.</param>
    /// <param name="resourcesJson">uBO scriptlets/redirects (Filters\ubo-resources.json shipped with the app); null = none.</param>
    public FilterEngineService(
        FilterListDownloader downloader,
        string filtersFolder,
        string? resourcesJson,
        AppLogger log,
        Func<DateTimeOffset>? now = null)
    {
        _downloader = downloader;
        _resourcesJson = resourcesJson;
        _log = log;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _listsFolder = Path.Combine(filtersFolder, "lists");
        _stateFile = Path.Combine(_listsFolder, "state.json");
        _engineFile = Path.Combine(filtersFolder, "engine.dat");
        _engineMetaFile = Path.Combine(filtersFolder, "engine.json");
    }

    /// <summary>The current engine, or null before the first lists are downloaded (or if the engine can't run).
    /// A replaced engine is disposed; a caller holding the old one just gets "allow" answers from then on.</summary>
    public IFilterEngine? Engine => Volatile.Read(ref _engine);

    /// <summary>Raised (on a thread-pool thread) after <see cref="Engine"/> changed.</summary>
    public event EventHandler? EngineChanged;

    /// <summary>Raised (on a thread-pool thread) when list status changed (downloads, errors, enabled set).</summary>
    public event EventHandler? StatusChanged;

    /// <summary>Why there is no engine although lists exist (DLL missing…); null when fine.</summary>
    public string? EngineProblem { get; private set; }

    public IReadOnlyList<string> EnabledListIds => Volatile.Read(ref _enabled);

    /// <summary>Loads what's on disk: the saved engine if it still matches the lists, else compiles the cached lists.
    /// Never downloads. Call once at startup (off the UI thread is fine; it's ~5 ms with a saved engine).</summary>
    public async Task InitializeAsync(IEnumerable<string>? enabledIds, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _enabled, FilterListCatalog.Normalize(enabledIds));
            lock (_stateLock)
            {
                _state = LoadState();
            }

            var fingerprint = Fingerprint();
            if (TryLoadSavedEngine(fingerprint))
            {
                return;
            }

            if (HasAnyEnabledContent())
            {
                await RebuildAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Downloads enabled lists that are due (all of them with <paramref name="force"/>), then rebuilds the
    /// engine if anything changed. A failed list keeps its last good copy and is retried after an hour.</summary>
    public async Task<FilterUpdateSummary> UpdateAsync(bool force, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await UpdateCoreAsync(force, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Changes which lists are used: downloads newly enabled ones that aren't cached yet and rebuilds.</summary>
    public async Task<FilterUpdateSummary> SetEnabledListsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _enabled, FilterListCatalog.Normalize(ids));
            StatusChanged?.Invoke(this, EventArgs.Empty);
            return await UpdateCoreAsync(force: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Background loop for the app: waits <paramref name="initialDelay"/> (so startup isn't slowed), then
    /// updates due lists every <paramref name="interval"/> until cancelled. Never throws except on cancellation.</summary>
    public async Task RunAsync(TimeSpan initialDelay, TimeSpan interval, CancellationToken cancellationToken)
    {
        await Task.Delay(initialDelay, cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await UpdateAsync(force: false, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Error("Filter list update round failed.", ex);
            }

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }

    public IReadOnlyList<FilterListStatus> GetStatus()
    {
        var enabled = new HashSet<string>(EnabledListIds, StringComparer.OrdinalIgnoreCase);
        lock (_stateLock)
        {
            return FilterListCatalog.All.Select(info =>
            {
                _state.TryGetValue(info.Id, out var s);
                return new FilterListStatus(info, enabled.Contains(info.Id), s?.UpdatedAt, s?.CheckedAt, s?.Rules ?? 0, s?.Error);
            }).ToArray();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Exchange(ref _engine, null)?.Dispose();
    }

    // ---------------------------------------------------------------------------------------------------------------

    private async Task<FilterUpdateSummary> UpdateCoreAsync(bool force, CancellationToken cancellationToken)
    {
        var now = _now();
        var due = EnabledListIds
            .Select(FilterListCatalog.Find)
            .OfType<FilterListInfo>()
            .Where(info => force || IsDue(info.Id, now))
            .ToArray();

        var changed = 0;
        var failed = 0;
        await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = ParallelDownloads, CancellationToken = cancellationToken },
            async (info, token) =>
            {
                switch (await DownloadOneAsync(info, token).ConfigureAwait(false))
                {
                    case true:
                        Interlocked.Increment(ref changed);
                        break;
                    case null:
                        Interlocked.Increment(ref failed);
                        break;
                }
            }).ConfigureAwait(false);

        if (due.Length > 0)
        {
            SaveState();
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        var rebuilt = false;
        if (Fingerprint() != _engineFingerprint || (Engine is null && HasAnyEnabledContent()))
        {
            rebuilt = await RebuildAsync(cancellationToken).ConfigureAwait(false);
        }

        if (due.Length > 0)
        {
            _log.Info($"Filter lists: {due.Length} checked, {changed} changed, {failed} failed{(rebuilt ? ", engine rebuilt" : "")}.");
        }

        return new FilterUpdateSummary(due.Length, changed, failed, rebuilt);
    }

    /// <summary>True = content changed, false = unchanged, null = failed.</summary>
    private async Task<bool?> DownloadOneAsync(FilterListInfo info, CancellationToken cancellationToken)
    {
        var now = _now();
        try
        {
            var list = await _downloader.DownloadAsync(info, cancellationToken).ConfigureAwait(false);
            var hash = Hash(list.Text);
            var expiry = Clamp(list.Expires ?? DefaultExpiry);
            bool contentChanged;
            lock (_stateLock)
            {
                _state.TryGetValue(info.Id, out var old);
                contentChanged = old?.ContentHash != hash || !File.Exists(ListFile(info.Id))
                                 || (old is not null && !FromCurrentAddress(info.Id, old));
            }

            if (contentChanged)
            {
                Directory.CreateDirectory(_listsFolder);
                await AtomicFile.WriteTextAsync(ListFile(info.Id), list.Text, cancellationToken).ConfigureAwait(false);
            }

            lock (_stateLock)
            {
                _state.TryGetValue(info.Id, out var old);
                _state[info.Id] = new ListState
                {
                    Title = list.Title,
                    SourceUrl = list.SourceUrl,
                    ContentHash = hash,
                    Rules = contentChanged ? CountRules(list.Text) : old?.Rules ?? CountRules(list.Text),
                    UpdatedAt = contentChanged ? now : old?.UpdatedAt ?? now,
                    CheckedAt = now,
                    NextCheckAt = now + expiry,
                    Error = null,
                };
            }

            return contentChanged;
        }
        catch (FilterListDownloadException ex)
        {
            _log.Warn(ex.Message);
            lock (_stateLock)
            {
                _state.TryGetValue(info.Id, out var old);
                _state[info.Id] = (old ?? new ListState()) with
                {
                    CheckedAt = now,
                    NextCheckAt = now + RetryAfterFailure,
                    Error = ex.Message,
                };
            }

            return null;
        }
    }

    private bool IsDue(string id, DateTimeOffset now)
    {
        lock (_stateLock)
        {
            // Due when never checked, when its saved copy went missing, or when its time came (the list's own expiry
            // after a success, RetryAfterFailure after a failure).
            return !_state.TryGetValue(id, out var s)
                   || s.NextCheckAt is not { } next
                   || now >= next
                   || (s.ContentHash is not null && !File.Exists(ListFile(id)))
                   || !FromCurrentAddress(id, s);
        }
    }

    private async Task<bool> RebuildAsync(CancellationToken cancellationToken)
    {
        var fingerprint = Fingerprint();
        var lists = new List<FilterListText>();
        foreach (var id in EnabledListIds)
        {
            if (FilterListCatalog.Find(id) is { } info && HasUsableCopy(id))
            {
                var text = await File.ReadAllTextAsync(ListFile(id), cancellationToken).ConfigureAwait(false);
                lists.Add(new FilterListText(text, info.ScriptletPermissions, info.HostsFormat));
            }
        }

        if (lists.Count == 0)
        {
            // Every list turned off (or nothing downloaded yet): no engine means nothing is blocked.
            ReplaceEngine(null, fingerprint);
            return true;
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var engine = await Task.Run(() => AdblockRustEngine.Build(lists, _resourcesJson), cancellationToken).ConfigureAwait(false);
            var bytes = engine.Serialize();
            var buildMs = stopwatch.ElapsedMilliseconds;
            await SaveEngineAsync(bytes, fingerprint, cancellationToken).ConfigureAwait(false);
            EngineProblem = null;
            ReplaceEngine(engine, fingerprint);
            _log.Info($"Ad-block engine built from {lists.Count} lists in {buildMs} ms ({bytes.Length / 1024} KB saved)" +
                      (engine.HasResources ? "." : "; scriptlets unavailable (no resources)."));
            return true;
        }
        catch (FilterEngineUnavailableException ex)
        {
            EngineProblem = ex.Message;
            _log.Error($"Ad-block engine unavailable: {ex.Message}");
            return false;
        }
    }

    private bool TryLoadSavedEngine(string fingerprint)
    {
        try
        {
            if (!File.Exists(_engineFile) || !File.Exists(_engineMetaFile)
                || JsonSerializer.Deserialize<EngineMeta>(File.ReadAllText(_engineMetaFile))?.Fingerprint != fingerprint)
            {
                return false;
            }

            var engine = AdblockRustEngine.TryDeserialize(File.ReadAllBytes(_engineFile), _resourcesJson);
            if (engine is null)
            {
                _log.Warn("The saved ad-block engine couldn't be read; rebuilding it from the lists.");
                return false;
            }

            ReplaceEngine(engine, fingerprint);
            return true;
        }
        catch (FilterEngineUnavailableException ex)
        {
            EngineProblem = ex.Message;
            _log.Error($"Ad-block engine unavailable: {ex.Message}");
            return true; // nothing more to try
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task SaveEngineAsync(byte[] bytes, string fingerprint, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_engineFile)!);
            await AtomicFile.WriteBytesAsync(_engineFile, bytes, cancellationToken).ConfigureAwait(false);
            await AtomicFile.WriteTextAsync(_engineMetaFile, JsonSerializer.Serialize(new EngineMeta(fingerprint), Json), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("Couldn't save the compiled ad-block engine; it will be rebuilt at the next start.", ex);
        }
    }

    private void ReplaceEngine(IFilterEngine? engine, string fingerprint)
    {
        if (_disposed)
        {
            engine?.Dispose();
            return;
        }

        _engineFingerprint = fingerprint;
        var old = Interlocked.Exchange(ref _engine, engine);
        if (!ReferenceEquals(old, engine))
        {
            old?.Dispose();
            EngineChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Identifies the engine's input: interface version, enabled lists in order, their trust and content.</summary>
    private string Fingerprint()
    {
        var text = new StringBuilder($"abi={NativeMethods.ExpectedAbiVersion}\n");
        lock (_stateLock)
        {
            foreach (var id in EnabledListIds)
            {
                _state.TryGetValue(id, out var s);
                var hash = s?.ContentHash is { } h && File.Exists(ListFile(id)) && FromCurrentAddress(id, s) ? h : "none";
                var info = FilterListCatalog.Find(id);
                text.Append(id).Append(':').Append(info?.ScriptletPermissions ?? 0).Append(info?.HostsFormat == true ? ":hosts" : "")
                    .Append(':').Append(hash).Append('\n');
            }
        }

        return Hash(text.ToString());
    }

    private bool HasAnyEnabledContent() => EnabledListIds.Any(HasUsableCopy);

    /// <summary>A saved copy exists and came from one of the list's current addresses.</summary>
    private bool HasUsableCopy(string id)
    {
        lock (_stateLock)
        {
            return File.Exists(ListFile(id)) && (!_state.TryGetValue(id, out var s) || FromCurrentAddress(id, s));
        }
    }

    /// <summary>
    /// False when the saved copy was downloaded from an address the catalogue no longer lists (for example Peter Lowe's
    /// list, moved from "adblock" to hosts format): that copy may be in the wrong format, so it isn't used and the list
    /// is downloaded again at the next check. Copies without a recorded address are trusted.
    /// </summary>
    private static bool FromCurrentAddress(string id, ListState s) =>
        s.SourceUrl is null || FilterListCatalog.Find(id) is not { } info || info.Urls.Contains(s.SourceUrl, StringComparer.OrdinalIgnoreCase);

    private string ListFile(string id) => Path.Combine(_listsFolder, id + ".txt");

    private Dictionary<string, ListState> LoadState()
    {
        try
        {
            if (File.Exists(_stateFile)
                && JsonSerializer.Deserialize<Dictionary<string, ListState>>(File.ReadAllText(_stateFile)) is { } loaded)
            {
                return new Dictionary<string, ListState>(loaded, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.Warn("Filter list state was unreadable; lists will be checked again.", ex);
        }

        return new Dictionary<string, ListState>(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveState()
    {
        string json;
        lock (_stateLock)
        {
            json = JsonSerializer.Serialize(_state, Json);
        }

        try
        {
            Directory.CreateDirectory(_listsFolder);
            AtomicFile.WriteText(_stateFile, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("Couldn't save filter list state.", ex);
        }
    }

    private static TimeSpan Clamp(TimeSpan expiry) =>
        expiry < MinExpiry ? MinExpiry : expiry > MaxExpiry ? MaxExpiry : expiry;

    internal static int CountRules(string text)
    {
        var count = 0;
        foreach (var line in text.AsSpan().EnumerateLines())
        {
            var trimmed = line.Trim();
            // '!' comments and the "[Adblock Plus 2.0]" header; '#' starts real rules here (##.ad, #@#…).
            if (trimmed.Length > 0 && trimmed[0] != '!' && trimmed[0] != '[')
            {
                count++;
            }
        }

        return count;
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    internal sealed record ListState
    {
        public string? Title { get; init; }
        public string? SourceUrl { get; init; }
        public string? ContentHash { get; init; }
        public int Rules { get; init; }
        public DateTimeOffset? UpdatedAt { get; init; }
        public DateTimeOffset? CheckedAt { get; init; }
        public DateTimeOffset? NextCheckAt { get; init; }
        public string? Error { get; init; }
    }

    private sealed record EngineMeta(string Fingerprint);
}
