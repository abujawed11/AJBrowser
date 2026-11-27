using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace AJBrowser.Core.Tools;

/// <summary>A tool AJBrowser installed itself (<c>Tools\&lt;tool&gt;\current.json</c>).</summary>
public sealed record InstalledTool(ToolKind Kind, string Version, string ExePath, DateTimeOffset InstalledAt, DateTimeOffset? LastCheckedAt);

/// <summary>A newer release than the installed one.</summary>
public sealed record ToolUpdate(ToolKind Kind, string Version, long Size);

/// <summary>Install progress: bytes of the download, then the stage ("Checking", "Unpacking", "Testing").</summary>
public sealed record ToolInstallProgress(long ReceivedBytes, long? TotalBytes, string Stage);

/// <summary>How an install or update ended. <see cref="Error"/> is a sentence for the user.</summary>
public sealed record ToolInstallResult(InstalledTool? Tool, string? Error)
{
    public bool Succeeded => Tool is not null;
}

/// <summary>
/// Finds, installs and updates the external tools of plan Phase 7.5 in <c>%LOCALAPPDATA%\AJBrowser\Tools\&lt;tool&gt;\&lt;version&gt;\</c>.
/// Installs only when the user asks (never silently): reads the official GitHub release, downloads the one asset
/// <see cref="ToolCatalog.SelectAsset"/> picks, and refuses it unless its SHA-256 matches the release's published digest
/// (or the project's own checksum file). Archives are unpacked by taking only the named program files (no paths from the
/// archive are used, so nothing can be written outside the tool's folder). The new program must answer its version
/// command before it replaces the old one; the previous version is kept until then.
/// </summary>
public sealed class ToolInstaller
{
    /// <summary>Nothing AJBrowser installs is bigger than this (FFmpeg's LGPL zip is ~172 MB).</summary>
    public const long MaxDownloadBytes = 400L * 1024 * 1024;

    /// <summary>How often the app checks for a newer yt-dlp (it goes stale on YouTube within weeks).</summary>
    public static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromDays(1);

    private const string StateFile = "current.json";

    private readonly HttpClient _http;
    private readonly string _root;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<ProcessResult>> _run;
    private readonly Func<DateTimeOffset> _now;

    /// <param name="toolsRoot"><c>%LOCALAPPDATA%\AJBrowser\Tools</c>.</param>
    /// <param name="run">Runs a program with arguments (tests fake it); default <see cref="ProcessRunner"/>.</param>
    public ToolInstaller(HttpClient http, string toolsRoot, Func<string, IReadOnlyList<string>, CancellationToken, Task<ProcessResult>>? run = null, Func<DateTimeOffset>? now = null)
    {
        _http = http;
        _root = toolsRoot;
        _run = run ?? ((exe, args, token) => ProcessRunner.RunAsync(exe, args, keepOutput: true, cancellationToken: token));
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public string FolderFor(ToolKind kind) => Path.Combine(_root, kind.ToString().ToLowerInvariant());

    // ---------------------------------------------------------------- finding

    /// <summary>The copy AJBrowser installed, if its program file is still there.</summary>
    public InstalledTool? GetInstalled(ToolKind kind)
    {
        try
        {
            var file = Path.Combine(FolderFor(kind), StateFile);
            if (!File.Exists(file))
            {
                return null;
            }

            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(file));
            if (state?.Version is null || state.Exe is null)
            {
                return null;
            }

            // The stored path is relative to the tool folder; anything escaping it is ignored.
            var exe = Path.GetFullPath(Path.Combine(FolderFor(kind), state.Exe));
            return exe.StartsWith(Path.GetFullPath(FolderFor(kind)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(exe)
                ? new InstalledTool(kind, state.Version, exe, state.InstalledAt, state.CheckedAt)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The program to use: the path chosen in Settings (a file, or a folder that holds it), else the copy AJBrowser
    /// installed, else the PATH. Null when there's none.
    /// </summary>
    public string? Locate(ToolKind kind, string? configuredPath, string? pathVariable = null)
    {
        var exeName = ToolCatalog.For(kind).ExeName;
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var candidate = configuredPath.Trim().Trim('"');
            if (Directory.Exists(candidate))
            {
                candidate = Path.Combine(candidate, exeName);
            }

            if (File.Exists(candidate) && candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(candidate);
            }
        }

        if (GetInstalled(kind) is { } installed)
        {
            return installed.ExePath;
        }

        foreach (var folder in (pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim('"'), exeName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry: skip it.
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- updates

    /// <summary>True when the installed copy hasn't been checked for a newer version in the last day.</summary>
    public bool IsUpdateCheckDue(ToolKind kind) =>
        GetInstalled(kind) is { } tool && (tool.LastCheckedAt is null || _now() - tool.LastCheckedAt >= UpdateCheckInterval);

    /// <summary>
    /// Asks GitHub for the latest release; returns it when it's newer than the installed copy (null when up to date,
    /// not installed, or GitHub can't be reached). Records the check time either way it got an answer.
    /// </summary>
    public async Task<ToolUpdate?> CheckForUpdateAsync(ToolKind kind, CancellationToken cancellationToken = default)
    {
        if (GetInstalled(kind) is not { } installed)
        {
            return null;
        }

        try
        {
            var (asset, version, _) = await GetLatestAsync(ToolCatalog.For(kind), cancellationToken).ConfigureAwait(false);
            WriteState(kind, installed with { LastCheckedAt = _now() });
            return ToolVersion.IsNewer(version, installed.Version) ? new ToolUpdate(kind, version, asset.Size) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException or ToolInstallException or IOException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- install

    /// <summary>
    /// Installs (or updates to) the latest release. Never throws for network, checksum, archive or file problems: the
    /// result carries a sentence for the user. Cancellation throws <see cref="OperationCanceledException"/> and leaves
    /// the installed copy as it was.
    /// </summary>
    public async Task<ToolInstallResult> InstallAsync(ToolKind kind, IProgress<ToolInstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var spec = ToolCatalog.For(kind);
        var folder = FolderFor(kind);
        string? download = null;
        string? versionFolder = null;
        try
        {
            var (asset, version, sha256) = await GetLatestAsync(spec, cancellationToken).ConfigureAwait(false);
            if (GetInstalled(kind) is { } current && !ToolVersion.IsNewer(version, current.Version))
            {
                WriteState(kind, current with { LastCheckedAt = _now() });
                return new ToolInstallResult(current with { LastCheckedAt = _now() }, null);
            }

            if (asset.Size > MaxDownloadBytes)
            {
                throw new ToolInstallException($"The {spec.DisplayName} download is unexpectedly large.");
            }

            Directory.CreateDirectory(folder);
            download = Path.Combine(folder, "download.part");
            await DownloadVerifiedAsync(asset, sha256, download, progress, cancellationToken).ConfigureAwait(false);

            progress?.Report(new ToolInstallProgress(asset.Size, asset.Size, "Unpacking"));
            versionFolder = Path.Combine(folder, SafeFolderName(version));
            if (Directory.Exists(versionFolder))
            {
                Directory.Delete(versionFolder, recursive: true);
            }

            Directory.CreateDirectory(versionFolder);
            if (asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ExtractProgramFiles(download, versionFolder, [spec.ExeName, .. spec.ExtraFiles], spec.DisplayName);
                File.Delete(download);
            }
            else
            {
                File.Move(download, Path.Combine(versionFolder, spec.ExeName));
            }

            progress?.Report(new ToolInstallProgress(asset.Size, asset.Size, "Testing"));
            var exe = Path.Combine(versionFolder, spec.ExeName);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                ProcessResult check;
                try
                {
                    check = await _run(exe, spec.VersionArguments, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    check = new ProcessResult(-1, [], ["timed out"]);
                }

                if (!check.Succeeded)
                {
                    throw new ToolInstallException($"{spec.DisplayName} was downloaded but didn't start. Your antivirus may have blocked it.");
                }
            }

            var tool = new InstalledTool(kind, version, exe, _now(), _now());
            WriteState(kind, tool);
            RemoveOldVersions(folder, versionFolder);
            return new ToolInstallResult(tool, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Cleanup(download, versionFolder, kind);
            throw;
        }
        catch (ToolInstallException ex)
        {
            Cleanup(download, versionFolder, kind);
            return new ToolInstallResult(null, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Cleanup(download, versionFolder, kind);
            return new ToolInstallResult(null, $"Couldn't download {spec.DisplayName}. Check the internet connection and try again.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
        {
            Cleanup(download, versionFolder, kind);
            return new ToolInstallResult(null, $"Couldn't install {spec.DisplayName}: {ex.Message}");
        }
    }

    /// <summary>The latest release's asset, its version, and the SHA-256 it must have.</summary>
    private async Task<(GitHubAsset Asset, string Version, string Sha256)> GetLatestAsync(ToolSpec spec, CancellationToken cancellationToken)
    {
        var json = await GetStringAsync(spec.ReleaseApiUrl, cancellationToken).ConfigureAwait(false);
        var release = GitHubRelease.Parse(json, spec.Repository);
        if (ToolCatalog.SelectAsset(spec, release) is not { } selected)
        {
            throw new ToolInstallException($"The latest {spec.DisplayName} release has no Windows download.");
        }

        var sha256 = selected.Asset.Sha256;
        if (sha256 is null)
        {
            // No digest from the API: the project's own checksum file (same release, same host rules).
            var checksumAsset = spec.ChecksumAsset is { } name
                ? release.Assets.FirstOrDefault(a => a.Name == name)
                : release.Assets.FirstOrDefault(a => a.Name == selected.Asset.Name + ".sha256sum");
            if (checksumAsset is not null)
            {
                sha256 = Checksums.Find(await GetStringAsync(checksumAsset.DownloadUrl, cancellationToken).ConfigureAwait(false), selected.Asset.Name);
            }
        }

        return sha256 is null
            ? throw new ToolInstallException($"The {spec.DisplayName} release doesn't publish a checksum, so it can't be verified. Nothing was installed.")
            : (selected.Asset, selected.Version, sha256);
    }

    private async Task<string> GetStringAsync(Uri url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AJBrowser", "1.0"));
        if (url.Host == "api.github.com")
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ToolInstallException(response.StatusCode == System.Net.HttpStatusCode.Forbidden
                ? "GitHub refused the request (too many requests from this network?). Try again later."
                : $"GitHub answered with HTTP {(int)response.StatusCode}. Try again later.");
        }

        if (response.Content.Headers.ContentLength > 4 * 1024 * 1024)
        {
            throw new ToolInstallException("GitHub's answer was unexpectedly large.");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Streams the asset to <paramref name="path"/> while hashing it; deletes it and fails on a mismatch.</summary>
    private async Task DownloadVerifiedAsync(GitHubAsset asset, string expectedSha256, string path, IProgress<ToolInstallProgress>? progress, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AJBrowser", "1.0"));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ToolInstallException($"The download failed (HTTP {(int)response.StatusCode}). Try again later.");
        }

        var total = response.Content.Headers.ContentLength ?? (asset.Size > 0 ? asset.Size : null);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81_920, useAsync: true))
        {
            var buffer = new byte[81_920];
            long received = 0;
            var lastReport = 0L;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                received += read;
                if (received > MaxDownloadBytes)
                {
                    throw new ToolInstallException("The download is unexpectedly large.");
                }

                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                if (received - lastReport >= 512 * 1024)
                {
                    lastReport = received;
                    progress?.Report(new ToolInstallProgress(received, total, "Downloading"));
                }
            }

            progress?.Report(new ToolInstallProgress(received, total, "Checking"));
        }

        var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (actual != expectedSha256)
        {
            File.Delete(path);
            throw new ToolInstallException("The download didn't match its published checksum, so it wasn't installed. Try again later.");
        }
    }

    /// <summary>Takes only the named files out of the archive (wherever they sit in it) into <paramref name="folder"/>.</summary>
    private static void ExtractProgramFiles(string zipPath, string folder, IReadOnlyList<string> fileNames, string displayName)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var fileName in fileNames)
        {
            var entry = archive.Entries
                .Where(e => string.Equals(e.Name, fileName, StringComparison.OrdinalIgnoreCase) && e.FullName.Count(c => c is '/' or '\\') <= 3)
                .OrderBy(e => e.FullName.Length)
                .FirstOrDefault() ?? throw new ToolInstallException($"The {displayName} download doesn't contain {fileName}.");
            if (entry.Length > MaxDownloadBytes)
            {
                throw new ToolInstallException($"{fileName} in the {displayName} download is unexpectedly large.");
            }

            entry.ExtractToFile(Path.Combine(folder, fileName), overwrite: true);
        }
    }

    private static string SafeFolderName(string version)
    {
        var safe = new string(version.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_').ToArray());
        return safe.Length is > 0 and <= 64 && safe.Trim('.').Length > 0 ? safe : "release";
    }

    private void WriteState(ToolKind kind, InstalledTool tool)
    {
        var folder = FolderFor(kind);
        var state = new State
        {
            Version = tool.Version,
            Exe = Path.GetRelativePath(folder, tool.ExePath),
            InstalledAt = tool.InstalledAt,
            CheckedAt = tool.LastCheckedAt,
        };
        var file = Path.Combine(folder, StateFile);
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(state));
        File.Move(file + ".tmp", file, overwrite: true);
    }

    /// <summary>Keeps the new version and the one before it (in case it's still running); removes older ones.</summary>
    private static void RemoveOldVersions(string folder, string keep)
    {
        var others = Directory.GetDirectories(folder)
            .Where(d => !string.Equals(Path.GetFullPath(d), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .Skip(1);
        foreach (var old in others)
        {
            try
            {
                Directory.Delete(old, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use: try again after the next update.
            }
        }
    }

    private void Cleanup(string? download, string? versionFolder, ToolKind kind)
    {
        try
        {
            if (download is not null && File.Exists(download))
            {
                File.Delete(download);
            }

            // Only remove a half-made version folder, never the one current.json points to.
            if (versionFolder is not null && Directory.Exists(versionFolder))
            {
                var installed = GetInstalled(kind);
                var inUse = installed is not null
                            && installed.ExePath.StartsWith(Path.GetFullPath(versionFolder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                if (!inUse)
                {
                    Directory.Delete(versionFolder, recursive: true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leftovers are replaced by the next install.
        }
    }

    private sealed class State
    {
        public string? Version { get; set; }
        public string? Exe { get; set; }
        public DateTimeOffset InstalledAt { get; set; }
        public DateTimeOffset? CheckedAt { get; set; }
    }
}

/// <summary>An install problem with a sentence for the user.</summary>
public sealed class ToolInstallException(string message) : Exception(message);
