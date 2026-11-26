using System.Text.Json;

namespace AJBrowser.Core.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON. Think of it as a tiny global store: read <see cref="Current"/>,
/// change it with <see cref="Update"/>, and listen to <see cref="Changed"/>.
/// <para>
/// Saving writes a temporary file and then replaces the real one, so a crash mid-write can't corrupt it.
/// A file that can't be read is kept as <c>settings.json.bad</c> and the defaults are used.
/// </para>
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly string _file;
    private readonly object _saveLock = new();

    private SettingsStore(string file, AppSettings current, string? loadProblem)
    {
        _file = file;
        Current = current;
        LoadProblem = loadProblem;
    }

    public AppSettings Current { get; private set; }

    /// <summary>Set when the file existed but couldn't be read (the defaults are in use). For the log.</summary>
    public string? LoadProblem { get; }

    /// <summary>Raised after <see cref="Current"/> changed, with the previous settings. On the caller's thread.</summary>
    public event EventHandler<AppSettings>? Changed;

    /// <summary>Loads the file, or uses the defaults if it's missing or unreadable. Never throws for a bad file.</summary>
    public static SettingsStore Load(string file)
    {
        if (!File.Exists(file))
        {
            return new SettingsStore(file, new AppSettings(), null);
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), JsonOptions) ?? new AppSettings();
            return new SettingsStore(file, loaded.Normalize(), null);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Copy(file, file + ".bad", overwrite: true);
            }
            catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException)
            {
                // Keeping a copy is a courtesy; defaults still work without it.
            }

            return new SettingsStore(file, new AppSettings(), $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Applies a change, saves it and raises <see cref="Changed"/>. Does nothing if the result is identical.</summary>
    public void Update(Func<AppSettings, AppSettings> change)
    {
        var previous = Current;
        var next = change(previous).Normalize();
        if (next == previous)
        {
            return;
        }

        Current = next;
        Save();
        Changed?.Invoke(this, previous);
    }

    /// <summary>Writes <see cref="Current"/> to disk (temp file + replace).</summary>
    public void Save()
    {
        lock (_saveLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
            var temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Current, JsonOptions));
            File.Move(temp, _file, overwrite: true);
        }
    }
}
