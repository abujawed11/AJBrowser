using AJBrowser.Core.Logging;
using Microsoft.Web.WebView2.Core;

namespace AJBrowser.Browser;

/// <summary>
/// Owns the single shared <see cref="CoreWebView2Environment"/> for AJBrowser's normal profile.
/// Every WebView must be created from this environment (WebView2 requires all WebViews that share a user
/// data folder to use identical environment options). It is created lazily on first use and can be
/// recreated after the browser process dies.
/// </summary>
public sealed class BrowserEnvironment
{
    /// <summary>Official download page for the Evergreen WebView2 Runtime.</summary>
    public static readonly Uri RuntimeDownloadUrl = new("https://developer.microsoft.com/microsoft-edge/webview2/");

    /// <summary>The profile normal tabs use (plan §5.7). Private windows get their own later.</summary>
    public const string DefaultProfileName = "Default";

    private readonly string _userDataFolder;
    private readonly AppLogger _log;
    private Task<CoreWebView2Environment>? _current;
    private CoreWebView2Profile? _profile;

    public BrowserEnvironment(string userDataFolder, AppLogger log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataFolder);
        _userDataFolder = userDataFolder;
        _log = log;
    }

    /// <summary>The installed WebView2 Runtime version, or null if no runtime is installed.</summary>
    public static string? GetInstalledRuntimeVersion()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Returns the shared environment, creating it on first call. Must be called on the UI thread.</summary>
    public Task<CoreWebView2Environment> GetAsync() => _current ??= CreateAsync();

    /// <summary>
    /// Creates a new environment after the browser process behind <paramref name="failed"/> died.
    /// If another tab already replaced it (or is replacing it), that result is reused, so several tabs
    /// recovering from the same crash end up sharing one new environment.
    /// </summary>
    public Task<CoreWebView2Environment> RecreateAsync(CoreWebView2Environment? failed)
    {
        if (_current is { IsCompleted: false } pending)
        {
            return pending;
        }

        if (failed is not null && _current is { IsCompletedSuccessfully: true } done && !ReferenceEquals(done.Result, failed))
        {
            return done;
        }

        return _current = CreateAsync();
    }

    /// <summary>Called by every new WebView: browsing data belongs to the profile, which is only reachable through a WebView.</summary>
    internal void RememberProfile(CoreWebView2Profile profile) => _profile = profile;

    /// <summary>
    /// Clears cookies, site storage, the disk cache and the engine's own history, from <paramref name="fromUtc"/>
    /// (null = all time) until now. Returns false if no web page has been opened yet (no profile to clear).
    /// </summary>
    public async Task<bool> ClearBrowsingDataAsync(DateTime? fromUtc)
    {
        if (_profile is null)
        {
            return false;
        }

        // Not passwords, autofill or settings: "clear browsing data" shouldn't wipe those.
        const CoreWebView2BrowsingDataKinds kinds =
            CoreWebView2BrowsingDataKinds.AllSite | CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.BrowsingHistory;

        if (fromUtc is { } from)
        {
            await _profile.ClearBrowsingDataAsync(kinds, DateTime.SpecifyKind(from, DateTimeKind.Utc), DateTime.UtcNow);
        }
        else
        {
            await _profile.ClearBrowsingDataAsync(kinds);
        }

        _log.Info($"Cleared cookies, site data and cache ({(fromUtc is null ? "all time" : "since " + fromUtc.Value.ToString("u"))})");
        return true;
    }

    public CoreWebView2ControllerOptions CreateControllerOptions(CoreWebView2Environment environment)
    {
        var options = environment.CreateCoreWebView2ControllerOptions();
        options.ProfileName = DefaultProfileName;
        options.IsInPrivateModeEnabled = false;
        return options;
    }

    private async Task<CoreWebView2Environment> CreateAsync()
    {
        try
        {
            // These options apply to every WebView in this user data folder; all of them must be created from here.
            var options = new CoreWebView2EnvironmentOptions
            {
                // Needed for the bundled ad blocker (uBlock Origin Lite, Phase 2.5). See ExtensionManager.
                AreBrowserExtensionsEnabled = true,
            };
            var environment = await CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: _userDataFolder, options);
            _log.Info($"WebView2 environment created. Runtime {environment.BrowserVersionString}");
            return environment;
        }
        catch
        {
            _current = null; // let the next GetAsync try again
            throw;
        }
    }
}
