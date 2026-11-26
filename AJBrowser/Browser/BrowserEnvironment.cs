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
