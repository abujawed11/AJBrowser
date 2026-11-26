namespace AJBrowser.Core.Infrastructure;

/// <summary>
/// Every per-user folder and file AJBrowser uses. Nothing is ever stored next to the executable,
/// because an installed app (for example under Program Files) cannot write there.
/// </summary>
public sealed class AppPaths
{
    public const string AppFolderName = "AJBrowser";

    public AppPaths(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        Root = Path.GetFullPath(root);
        WebView2UserData = Path.Combine(Root, "WebView2");
        Data = Path.Combine(Root, "Data");
        DatabaseFile = Path.Combine(Data, "ajbrowser.db");
        SettingsFile = Path.Combine(Data, "settings.json");
        Filters = Path.Combine(Root, "Filters");
        Logs = Path.Combine(Root, "Logs");
        Temp = Path.Combine(Root, "Temp");
        Tools = Path.Combine(Root, "Tools");
    }

    /// <summary>%LOCALAPPDATA%\AJBrowser — local (not roaming) because browser data is large and machine-specific.</summary>
    public static AppPaths CreateDefault() =>
        new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
            AppFolderName));

    public string Root { get; }

    /// <summary>WebView2 user data folder: cookies, cache and site storage.</summary>
    public string WebView2UserData { get; }

    public string Data { get; }
    public string DatabaseFile { get; }
    public string SettingsFile { get; }

    /// <summary>Downloaded ad-block filter lists and their compiled cache.</summary>
    public string Filters { get; }

    /// <summary>Redacted application logs.</summary>
    public string Logs { get; }

    /// <summary>Partial media segments and other scratch files; safe to clean.</summary>
    public string Temp { get; }

    /// <summary>External tools fetched on demand (for example FFmpeg).</summary>
    public string Tools { get; }

    public IReadOnlyList<string> AllDirectories => [Root, WebView2UserData, Data, Filters, Logs, Temp, Tools];

    /// <summary>Creates any missing directories. Safe to call repeatedly.</summary>
    public void EnsureCreated()
    {
        foreach (var directory in AllDirectories)
        {
            Directory.CreateDirectory(directory);
        }
    }
}
