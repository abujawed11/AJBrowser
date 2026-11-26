using System.Text.Json;

namespace AJBrowser.Core.AdBlocking;

/// <summary>
/// What AJBrowser remembers about the installed ad-block extension between runs: which extension in the
/// WebView2 profile is ours, which version and folder it was installed from, and whether the user turned it off.
/// WebView2 doesn't report an extension's version, so we have to keep that ourselves.
/// <para>Saved as a small JSON file until Phase 3 adds app settings.</para>
/// </summary>
public sealed record AdBlockerState
{
    /// <summary>The extension id WebView2 assigned when we installed it (unpacked extensions get an id from their folder path).</summary>
    public string? ExtensionId { get; init; }

    /// <summary>The version we installed, in <see cref="ExtensionVersion.Format"/> form.</summary>
    public string? Version { get; init; }

    /// <summary>The folder WebView2 loads the extension from.</summary>
    public string? Folder { get; init; }

    /// <summary>The user's on/off choice. Ads are blocked by default.</summary>
    public bool Enabled { get; init; } = true;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Loads the state, or returns the defaults if the file is missing or unreadable (never throws).</summary>
    public static AdBlockerState Load(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return new AdBlockerState();
            }

            return JsonSerializer.Deserialize<AdBlockerState>(File.ReadAllText(file), JsonOptions) ?? new AdBlockerState();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AdBlockerState();
        }
    }

    /// <summary>Saves via a temporary file and a rename, so a crash mid-write can't leave a corrupt file.</summary>
    public void Save(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, file, overwrite: true);
    }
}
