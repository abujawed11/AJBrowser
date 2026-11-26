using System.Text.Json.Serialization;
using AJBrowser.Core.Navigation;

namespace AJBrowser.Core.Settings;

/// <summary>What AJBrowser shows when it starts.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StartupBehavior>))]
public enum StartupBehavior
{
    /// <summary>One tab with the home page.</summary>
    HomePage,

    /// <summary>The tabs that were open last time ("Continue where you left off").</summary>
    RestoreSession,
}

[JsonConverter(typeof(JsonStringEnumConverter<ThemeSetting>))]
public enum ThemeSetting
{
    System,
    Light,
    Dark,
}

/// <summary>
/// User settings, saved as <c>Data\settings.json</c>. Immutable: change them with <c>settings with { … }</c>
/// through <see cref="SettingsStore.Update"/>. Missing fields in the file get the defaults below, so new settings
/// can be added without migrating the file.
/// <para>The ad blocker's on/off switch is not here: it stays with the extension's own state file (decision 2026-11-26, plan §12).</para>
/// </summary>
public sealed record AppSettings
{
    public const string DefaultHomePage = "https://www.bing.com/";

    public string HomePage { get; init; } = DefaultHomePage;

    public StartupBehavior Startup { get; init; } = StartupBehavior.HomePage;

    /// <summary>A preset id from <see cref="SearchEngines.Presets"/>, or <see cref="SearchEngines.CustomId"/>.</summary>
    public string SearchEngine { get; init; } = SearchEngines.Default.Id;

    /// <summary>Used when <see cref="SearchEngine"/> is "custom": an http(s) URL containing {query}.</summary>
    public string CustomSearchTemplate { get; init; } = string.Empty;

    public ThemeSetting Theme { get; init; } = ThemeSetting.System;

    public bool ShowBookmarksBar { get; init; } = true;

    /// <summary>Where downloads go (Phase 4). Null = the Windows Downloads folder.</summary>
    public string? DownloadFolder { get; init; }

    /// <summary>Ask where to save each download (Phase 4).</summary>
    public bool AskWhereToSave { get; init; }

    /// <summary>
    /// Optional ffmpeg.exe (a file or its folder) for turning TS streams into MP4 (Phase 6). Null: look on the PATH;
    /// without FFmpeg, TS streams are saved as .ts.
    /// </summary>
    public string? FfmpegPath { get; init; }

    /// <summary>Look for videos on pages and offer them in the toolbar (Phase 5). On by default.</summary>
    public bool DetectVideos { get; init; } = true;

    public const int MinConcurrentDownloads = 1;
    public const int MaxConcurrentDownloadsLimit = 10;

    /// <summary>
    /// How many of AJBrowser's own downloads (detected videos, from Phase 5) run at once; the rest wait their turn.
    /// Normal browser downloads aren't limited (the engine runs them).
    /// </summary>
    public int MaxConcurrentDownloads { get; init; } = Downloads.DownloadManager.DefaultMaxConcurrent;

    /// <summary>The home page as a URI (falls back to the default if the stored text is invalid).</summary>
    [JsonIgnore]
    public Uri HomePageUri => TryParseHomePage(HomePage) ?? new Uri(DefaultHomePage);

    /// <summary>
    /// Accepts what a home page may be: an http(s) or file URL, or "about:blank". Text without a scheme
    /// ("example.com") gets https://. Returns null for anything else (including javascript: and searches).
    /// </summary>
    public static Uri? TryParseHomePage(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (!trimmed.Contains("://", StringComparison.Ordinal) && !trimmed.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "https://" + trimmed;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var ok = ((uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && uri.Host.Length > 0)
                 || uri.Scheme == Uri.UriSchemeFile
                 || uri.AbsoluteUri.Equals("about:blank", StringComparison.OrdinalIgnoreCase);
        return ok ? uri : null;
    }

    /// <summary>Replaces invalid values (hand-edited file, older or newer version) with defaults.</summary>
    public AppSettings Normalize()
    {
        var result = this;

        if (TryParseHomePage(HomePage) is null)
        {
            result = result with { HomePage = DefaultHomePage };
        }

        var isCustom = string.Equals(SearchEngine, SearchEngines.CustomId, StringComparison.OrdinalIgnoreCase);
        if ((isCustom && !UrlOrSearchResolver.IsValidSearchTemplate(CustomSearchTemplate))
            || (!isCustom && SearchEngines.FindPreset(SearchEngine) is null))
        {
            result = result with { SearchEngine = SearchEngines.Default.Id };
        }

        if (result.CustomSearchTemplate is null)
        {
            result = result with { CustomSearchTemplate = string.Empty };
        }

        if (!Enum.IsDefined(Startup))
        {
            result = result with { Startup = StartupBehavior.HomePage };
        }

        if (!Enum.IsDefined(Theme))
        {
            result = result with { Theme = ThemeSetting.System };
        }

        if (MaxConcurrentDownloads is < MinConcurrentDownloads or > MaxConcurrentDownloadsLimit)
        {
            result = result with { MaxConcurrentDownloads = Math.Clamp(MaxConcurrentDownloads, MinConcurrentDownloads, MaxConcurrentDownloadsLimit) };
        }

        if (string.IsNullOrWhiteSpace(FfmpegPath))
        {
            result = result with { FfmpegPath = null };
        }

        if (string.IsNullOrWhiteSpace(DownloadFolder))
        {
            result = result with { DownloadFolder = null };
        }

        return result;
    }
}
