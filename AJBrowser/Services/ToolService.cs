using AJBrowser.Core.Logging;
using AJBrowser.Core.Settings;
using AJBrowser.Core.Tools;

namespace AJBrowser.Services;

/// <summary>
/// The external tools as the app sees them (plan Phase 7.5b): which copy of yt-dlp, FFmpeg and Deno to use (your own
/// path from Settings, else the copy AJBrowser installed, else the PATH), installing or updating AJBrowser's copy when
/// you click, a status line for Settings, and the daily background update of yt-dlp.
/// </summary>
public sealed class ToolService
{
    private readonly ToolInstaller _installer;
    private readonly SettingsStore _settings;
    private readonly AppLogger _log;
    private readonly HashSet<ToolKind> _installing = [];

    public ToolService(ToolInstaller installer, SettingsStore settings, AppLogger log)
    {
        _installer = installer;
        _settings = settings;
        _log = log;
    }

    /// <summary>A tool was installed, updated, or its path changed (Settings refreshes its status lines).</summary>
    public event EventHandler? Changed;

    /// <summary>The program to run right now, or null.</summary>
    public string? Locate(ToolKind kind) => _installer.Locate(kind, ConfiguredPath(kind));

    public string? ConfiguredPath(ToolKind kind) => kind switch
    {
        ToolKind.YtDlp => _settings.Current.YtDlpPath,
        ToolKind.Ffmpeg => _settings.Current.FfmpegPath,
        _ => _settings.Current.DenoPath,
    };

    /// <summary>Uses your own copy (a file or its folder); null goes back to AJBrowser's copy or the PATH.</summary>
    public void SetPath(ToolKind kind, string? path)
    {
        _settings.Update(s => kind switch
        {
            ToolKind.YtDlp => s with { YtDlpPath = path },
            ToolKind.Ffmpeg => s with { FfmpegPath = path },
            _ => s with { DenoPath = path },
        });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public InstalledTool? Installed(ToolKind kind) => _installer.GetInstalled(kind);

    public bool IsInstalling(ToolKind kind)
    {
        lock (_installing)
        {
            return _installing.Contains(kind);
        }
    }

    /// <summary>"Installed by AJBrowser: version 2026.11.23", "Your copy: D:\…", "Found on the PATH: …", or what it's for.</summary>
    public string Status(ToolKind kind)
    {
        var configured = ConfiguredPath(kind);
        var found = Locate(kind);
        var installed = _installer.GetInstalled(kind);
        if (configured is not null && found is not null && (installed is null || !PathEquals(found, installed.ExePath)))
        {
            return $"Your copy: {found}";
        }

        var prefix = configured is not null ? "Not found at the chosen place. " : string.Empty;
        if (installed is not null && found is not null && PathEquals(found, installed.ExePath))
        {
            return $"{prefix}Installed by AJBrowser: version {installed.Version}";
        }

        if (found is not null)
        {
            return $"{prefix}Found on the PATH: {found}";
        }

        return prefix + kind switch
        {
            ToolKind.YtDlp => "Not installed (about 24 MB). Needed for YouTube, Facebook, Instagram and other big sites.",
            ToolKind.Deno => "Not installed (about 43 MB). YouTube needs it: yt-dlp uses it to read YouTube's player.",
            _ => "Not installed (about 172 MB). Joins picture and sound (most big sites above 360p) and turns .ts streams into .mp4.",
        };
    }

    /// <summary>Installs or updates AJBrowser's copy; one at a time per tool. Never throws for install problems.</summary>
    public async Task<ToolInstallResult> InstallAsync(ToolKind kind, IProgress<ToolInstallProgress>? progress = null)
    {
        lock (_installing)
        {
            if (!_installing.Add(kind))
            {
                return new ToolInstallResult(null, "Already installing.");
            }
        }

        try
        {
            var before = _installer.GetInstalled(kind)?.Version;
            var result = await _installer.InstallAsync(kind, progress);
            if (result.Tool is { } tool)
            {
                _log.Info(before == tool.Version
                    ? $"{ToolCatalog.For(kind).DisplayName} is up to date ({tool.Version})"
                    : $"Installed {ToolCatalog.For(kind).DisplayName} {tool.Version}" + (before is null ? string.Empty : $" (was {before})"));
            }
            else
            {
                _log.Warn($"Installing {ToolCatalog.For(kind).DisplayName} failed: {result.Error}");
            }

            return result;
        }
        finally
        {
            lock (_installing)
            {
                _installing.Remove(kind);
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The daily yt-dlp update (Settings → "Update yt-dlp automatically"): only AJBrowser's own copy (never a path you
    /// chose), only when yt-dlp downloads are on, at most once a day, and not while <paramref name="isBusy"/> says a
    /// yt-dlp download is running. The new version is verified like a first install; the old one stays if anything fails.
    /// </summary>
    public async Task UpdateYtDlpIfDueAsync(Func<bool> isBusy)
    {
        var s = _settings.Current;
        if (!s.YtDlpEnabled || !s.AutoUpdateYtDlp || s.YtDlpPath is not null || !_installer.IsUpdateCheckDue(ToolKind.YtDlp))
        {
            return;
        }

        var update = await _installer.CheckForUpdateAsync(ToolKind.YtDlp);
        if (update is null)
        {
            return;
        }

        if (isBusy())
        {
            _log.Info($"yt-dlp {update.Version} is available; it will be installed when no yt-dlp download is running");
            return;
        }

        await InstallAsync(ToolKind.YtDlp);
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
