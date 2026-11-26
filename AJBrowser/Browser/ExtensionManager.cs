using System.IO;
using AJBrowser.Core.AdBlocking;
using AJBrowser.Core.Logging;
using Microsoft.Web.WebView2.Core;

namespace AJBrowser.Browser;

/// <summary>
/// Installs and controls the bundled ad blocker, uBlock Origin Lite (spike F15, plan Phase 2.5).
/// <para>
/// The app ships the official release .zip unmodified in <c>Extensions\</c> next to the exe. On first run (and
/// whenever a newer zip ships) it is unpacked to <c>%LOCALAPPDATA%\AJBrowser\Extensions\uBOLite\&lt;version&gt;\</c>
/// and added to the WebView2 profile. WebView2 loads unpacked extensions from that folder in place, which is
/// why we copy it out of the install folder (that one can change on app updates).
/// </para>
/// <para>
/// Extensions belong to the WebView2 profile, and a profile is only reachable through a WebView, so every
/// <see cref="BrowserTab"/> calls <see cref="PrepareAsync"/> once its WebView exists. The real work runs once.
/// Failures never stop browsing: the log says why and tracking prevention still works.
/// </para>
/// </summary>
public sealed class ExtensionManager
{
    /// <summary>The extension's name as WebView2 reports it (its manifest name is localized).</summary>
    public const string ExtensionName = "uBlock Origin Lite";

    /// <summary>Official release file name: uBOLite_&lt;version&gt;.chromium.zip.</summary>
    private const string PackagePattern = "uBOLite_*.zip";

    /// <summary>
    /// How long the first page waits for the ad blocker to be installed. Installing it the first time
    /// (unpacking about 1,000 files) takes a few seconds. After this, pages load anyway and the install finishes in the background.
    /// </summary>
    private static readonly TimeSpan FirstPageWaitLimit = TimeSpan.FromSeconds(20);

    private readonly string _bundleFolder;
    private readonly string _installFolder;
    private readonly string _stateFile;
    private readonly AppLogger _log;

    private AdBlockerState _state = new();
    private CoreWebView2Profile? _profile;
    private Task? _setup;
    private Task? _firstWait;

    /// <param name="bundleFolder">Folder next to the exe that holds the bundled zip.</param>
    /// <param name="extensionsRoot">Per-user extensions folder (<c>AppPaths.Extensions</c>).</param>
    public ExtensionManager(string bundleFolder, string extensionsRoot, AppLogger log)
    {
        _bundleFolder = bundleFolder;
        _installFolder = Path.Combine(extensionsRoot, "uBOLite");
        _stateFile = Path.Combine(_installFolder, "state.json");
        _log = log;
    }

    /// <summary>The ad blocker is installed and can be switched on and off.</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>The user's choice (on by default). Only takes effect while <see cref="IsAvailable"/>.</summary>
    public bool IsEnabled => _state.Enabled;

    /// <summary>The installed version, for display (for example "2026.1124.1811").</summary>
    public string? Version => IsAvailable ? _state.Version : null;

    /// <summary>uBlock Origin Lite's own settings page (filtering mode, per-site exceptions), or null if not installed.</summary>
    public Uri? SettingsPageUrl =>
        IsAvailable && _state.ExtensionId is { Length: > 0 } id ? new Uri($"chrome-extension://{id}/dashboard.html") : null;

    /// <summary>Raised on the UI thread when <see cref="IsAvailable"/>, <see cref="IsEnabled"/> or <see cref="Version"/> changed.</summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Called by every new WebView before its first navigation, on the UI thread. The first call starts the setup;
    /// the first WebViews wait for it (up to <see cref="FirstPageWaitLimit"/>) so their pages are already ad-free.
    /// Later calls return at once. Never throws.
    /// </summary>
    public Task PrepareAsync(CoreWebView2 core)
    {
        try
        {
            // Keep the newest profile object: after a browser-process crash the old one is dead.
            _profile = core.Profile;
        }
        catch (Exception ex)
        {
            _log.Warn("Could not get the WebView2 profile for the ad blocker", ex);
            return Task.CompletedTask;
        }

        _setup ??= SetUpAsync(_profile);
        return _firstWait ??= WaitForFirstSetupAsync(_setup);
    }

    /// <summary>Turns ad blocking on or off and remembers the choice. Never throws; problems are logged.</summary>
    public async Task SetEnabledAsync(bool enabled)
    {
        if (!IsAvailable || _profile is null || _state.ExtensionId is null)
        {
            return;
        }

        try
        {
            var extension = (await _profile.GetBrowserExtensionsAsync()).FirstOrDefault(e => e.Id == _state.ExtensionId);
            if (extension is null)
            {
                _log.Warn("The ad blocker is no longer installed in the profile; it will be reinstalled on the next start");
                IsAvailable = false;
            }
            else
            {
                await extension.EnableAsync(enabled);
                _state = _state with { Enabled = enabled };
                SaveState();
                _log.Info($"Ad blocking turned {(enabled ? "on" : "off")}");
            }
        }
        catch (Exception ex)
        {
            _log.Error("Could not switch the ad blocker", ex);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------------------------------------------------------- setup

    private async Task WaitForFirstSetupAsync(Task setup)
    {
        var finished = await Task.WhenAny(setup, Task.Delay(FirstPageWaitLimit));
        if (finished != setup)
        {
            _log.Warn($"Ad blocker setup is taking longer than {FirstPageWaitLimit.TotalSeconds:0} s; loading pages anyway");
        }
    }

    /// <summary>Runs once per app start. Never throws.</summary>
    private async Task SetUpAsync(CoreWebView2Profile profile)
    {
        try
        {
            // File work runs on a background thread; the awaits return to the UI thread, where WebView2 must be called.
            var package = await Task.Run(() => ExtensionPackage.FindNewest(_bundleFolder, PackagePattern));
            if (package is null)
            {
                _log.Warn($"No bundled ad blocker ({PackagePattern}) found in {_bundleFolder}; browsing continues without ad blocking");
                return;
            }

            _state = await Task.Run(() => AdBlockerState.Load(_stateFile));

            var all = await profile.GetBrowserExtensionsAsync();
            var ours = all
                .Where(e => (_state.ExtensionId is not null && e.Id == _state.ExtensionId)
                            || string.Equals(e.Name, ExtensionName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var installed = ours.FirstOrDefault(e => e.Id == _state.ExtensionId) ?? ours.FirstOrDefault();

            var folderExists = _state.Folder is { Length: > 0 } stateFolder && File.Exists(Path.Combine(stateFolder, "manifest.json"));
            var action = AdBlockerInstallPlanner.Decide(package.Version, installed is not null, ExtensionVersion.TryParse(_state.Version), folderExists);

            if (action == AdBlockerInstallAction.Replace)
            {
                foreach (var old in ours)
                {
                    await old.RemoveAsync();
                }

                _log.Info($"Removed the previous ad blocker (version {_state.Version ?? "unknown"}) to install {package.FolderName}");
            }

            if (action != AdBlockerInstallAction.KeepInstalled)
            {
                var folder = await Task.Run(() => package.UnpackTo(_installFolder));
                installed = await profile.AddBrowserExtensionAsync(folder);

                // The user's on/off choice survives upgrades; everything else describes the new install.
                _state = _state with { ExtensionId = installed.Id, Version = package.FolderName, Folder = folder };
                SaveState();
                _log.Info($"Installed {ExtensionName} {package.FolderName} (id {installed.Id})");

                var locked = await Task.Run(() => ExtensionPackage.DeleteOtherVersions(_installFolder, folder));
                if (locked.Count > 0)
                {
                    _log.Debug($"{locked.Count} old ad blocker folder(s) couldn't be deleted yet; will retry next start");
                }
            }

            // Make the profile match the remembered choice (also covers a reinstall, which starts enabled).
            if (installed!.IsEnabled != _state.Enabled)
            {
                await installed.EnableAsync(_state.Enabled);
            }

            IsAvailable = true;
            _log.Info($"Ad blocker ready: {ExtensionName} {_state.Version}, {(_state.Enabled ? "on" : "off")}");
        }
        catch (Exception ex)
        {
            // For example an old WebView2 Runtime without extension support, or a policy that blocks extensions.
            IsAvailable = false;
            _log.Error("The ad blocker could not be set up; browsing continues without it (tracking prevention stays on)", ex);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SaveState()
    {
        try
        {
            _state.Save(_stateFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("Could not save the ad blocker state", ex);
        }
    }
}
