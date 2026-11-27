using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using AJBrowser.Core.Tools;

namespace AJBrowser.Core.Tests.Infrastructure;

/// <summary>A fake GitHub: URL → bytes, request log.</summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    public ConcurrentDictionary<string, byte[]> Files { get; } = new();
    public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

    public void Add(string url, string text) => Files[url] = Encoding.UTF8.GetBytes(text);

    public int CountFor(string url) => Requests.Count(r => r.RequestUri!.AbsoluteUri == url);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        return Task.FromResult(Files.TryGetValue(request.RequestUri!.AbsoluteUri, out var data)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

public sealed class ToolCatalogTests
{
    private const string YtDlpApi = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";

    internal static string ReleaseJson(string repo, string tag, params (string Name, string? Sha, long Size, string? UrlOverride)[] assets) =>
        $$"""{"tag_name":"{{tag}}","published_at":"2026-11-23T23:49:56Z","assets":[""" +
        string.Join(",", assets.Select(a =>
            $$"""{"name":"{{a.Name}}","size":{{a.Size}},"browser_download_url":"{{a.UrlOverride ?? $"https://github.com/{repo}/releases/download/{tag}/{a.Name}"}}"{{(a.Sha is null ? "" : $",\"digest\":\"sha256:{a.Sha}\"")}}}""")) +
        "]}";

    [Fact]
    public void Release_parsing_keeps_only_the_repositorys_own_https_downloads()
    {
        var sha = new string('a', 64);
        var json = ReleaseJson("yt-dlp/yt-dlp", "2026.11.23",
            ("yt-dlp.exe", sha.ToUpperInvariant(), 23_627_865, null),
            ("evil.exe", sha, 1, "https://evil.example/yt-dlp/yt-dlp/releases/download/x/evil.exe"),
            ("other.exe", sha, 1, "https://github.com/someone/else/releases/download/x/other.exe"),
            ("plain.exe", sha, 1, "http://github.com/yt-dlp/yt-dlp/releases/download/x/plain.exe"));
        var release = GitHubRelease.Parse(json, "yt-dlp/yt-dlp");
        Assert.Equal("2026.11.23", release.Tag);
        var asset = Assert.Single(release.Assets);
        Assert.Equal(("yt-dlp.exe", sha, 23_627_865L), (asset.Name, asset.Sha256, asset.Size)); // digest normalised to lower case
        Assert.Throws<FormatException>(() => GitHubRelease.Parse("""{"message":"Not Found"}""", "yt-dlp/yt-dlp"));
        Assert.Throws<FormatException>(() => GitHubRelease.Parse("<html>", "yt-dlp/yt-dlp"));
    }

    [Fact]
    public void Selects_the_right_file_and_version_for_each_tool()
    {
        var yt = GitHubRelease.Parse(ReleaseJson("yt-dlp/yt-dlp", "2026.11.23", ("yt-dlp_x86.exe", null, 1, null), ("yt-dlp.exe", null, 1, null)), "yt-dlp/yt-dlp");
        Assert.Equal(("yt-dlp.exe", "2026.11.23"), (ToolCatalog.SelectAsset(ToolCatalog.YtDlp, yt)!.Value.Asset.Name, ToolCatalog.SelectAsset(ToolCatalog.YtDlp, yt)!.Value.Version));

        var deno = GitHubRelease.Parse(ReleaseJson("denoland/deno", "v2.9.8", ("deno-aarch64-pc-windows-msvc.zip", null, 1, null), ("deno-x86_64-pc-windows-msvc.zip", null, 1, null)), "denoland/deno");
        Assert.Equal(("deno-x86_64-pc-windows-msvc.zip", "2.9.8"), (ToolCatalog.SelectAsset(ToolCatalog.Deno, deno)!.Value.Asset.Name, ToolCatalog.SelectAsset(ToolCatalog.Deno, deno)!.Value.Version));

        var ffmpeg = GitHubRelease.Parse(ReleaseJson("FFmpeg-Builds/FFmpeg-Builds", "latest",
            ("ffmpeg-master-latest-win64-lgpl.zip", null, 1, null),
            ("ffmpeg-n9.0-latest-win64-gpl-9.0.zip", null, 1, null),
            ("ffmpeg-n8.1-latest-win64-lgpl-8.1.zip", null, 1, null),
            ("ffmpeg-n9.0-latest-win64-lgpl-9.0.zip", null, 1, null),
            ("ffmpeg-n9.0-latest-win64-lgpl-shared-9.0.zip", null, 1, null),
            ("ffmpeg-n9.1-latest-win64-lgpl-9.0.zip", null, 1, null)), "FFmpeg-Builds/FFmpeg-Builds");
        var chosen = ToolCatalog.SelectAsset(ToolCatalog.Ffmpeg, ffmpeg)!.Value;
        Assert.Equal(("ffmpeg-n9.0-latest-win64-lgpl-9.0.zip", "9.0"), (chosen.Asset.Name, chosen.Version)); // newest stable LGPL, never GPL/master

        Assert.Null(ToolCatalog.SelectAsset(ToolCatalog.YtDlp, GitHubRelease.Parse(ReleaseJson("yt-dlp/yt-dlp", "1", ("yt-dlp", null, 1, null)), "yt-dlp/yt-dlp")));
    }

    [Fact]
    public void Reads_checksum_files_in_both_formats()
    {
        var sha = new string('b', 64);
        Assert.Equal(sha, Checksums.Find($"{new string('c', 64)}  yt-dlp\n{sha.ToUpperInvariant()}  yt-dlp.exe\n", "yt-dlp.exe"));
        Assert.Equal(sha, Checksums.Find($"{sha} *ffmpeg-n9.0-latest-win64-lgpl-9.0.zip", "ffmpeg-n9.0-latest-win64-lgpl-9.0.zip"));
        Assert.Equal(sha, Checksums.Find($"\r\nAlgorithm : SHA256\r\nHash      : {sha.ToUpperInvariant()}\r\nPath      : C:\\a\\deno.zip\r\n", "deno-x86_64-pc-windows-msvc.zip"));
        Assert.Null(Checksums.Find($"{sha}  other.exe", "yt-dlp.exe"));
        Assert.Null(Checksums.NormalizeSha256("xyz"));
    }

    [Theory]
    [InlineData("2026.11.23", "2026.10.30", true)]
    [InlineData("2026.11.23", "2026.11.23", false)]
    [InlineData("2.10.0", "2.9.8", true)]
    [InlineData("v2.9.8", "2.9.8", false)]
    [InlineData("9.0", "8.1", true)]
    [InlineData("2026.11.23.1", "2026.11.23", true)]
    [InlineData("1.0", null, true)]
    public void Compares_versions_as_numbers(string candidate, string? installed, bool newer) =>
        Assert.Equal(newer, ToolVersion.IsNewer(candidate, installed));

    [Fact]
    public void Api_addresses() =>
        Assert.Equal(YtDlpApi, ToolCatalog.YtDlp.ReleaseApiUrl.AbsoluteUri);
}

public sealed class ToolInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AJBrowserTests", Guid.NewGuid().ToString("N"));
    private readonly FakeGitHub _github = new();
    private readonly List<(string Exe, IReadOnlyList<string> Args)> _runs = [];
    private int _exitCode;
    private DateTimeOffset _now = new(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ToolInstaller Create() => new(new HttpClient(_github), Path.Combine(_root, "Tools"), (exe, args, _) =>
    {
        _runs.Add((exe, args));
        return Task.FromResult(new ProcessResult(_exitCode, ["version"], []));
    }, () => _now);

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private byte[] ServeYtDlp(string tag, bool digest = true, bool checksumFile = false, byte[]? body = null)
    {
        var exe = body ?? Encoding.UTF8.GetBytes("MZ fake yt-dlp " + tag);
        var url = $"https://github.com/yt-dlp/yt-dlp/releases/download/{tag}/yt-dlp.exe";
        _github.Files[url] = exe;
        var assets = new List<(string, string?, long, string?)> { ("yt-dlp.exe", digest ? Sha(exe) : null, exe.Length, null) };
        if (checksumFile)
        {
            _github.Add($"https://github.com/yt-dlp/yt-dlp/releases/download/{tag}/SHA2-256SUMS", $"{new string('0', 64)}  yt-dlp\n{Sha(exe)}  yt-dlp.exe\n");
            assets.Add(("SHA2-256SUMS", null, 100, null));
        }

        _github.Add("https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest", ToolCatalogTests.ReleaseJson("yt-dlp/yt-dlp", tag, [.. assets]));
        return exe;
    }

    private static byte[] Zip(params (string Path, string Content)[] entries)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open());
                writer.Write(content);
            }
        }

        return memory.ToArray();
    }

    [Fact]
    public async Task Installs_a_verified_exe_and_remembers_it()
    {
        var exe = ServeYtDlp("2026.11.23");
        var installer = Create();
        var progress = new List<ToolInstallProgress>();

        var result = await installer.InstallAsync(ToolKind.YtDlp, new SyncProgress<ToolInstallProgress>(progress.Add));

        Assert.True(result.Succeeded, result.Error);
        var path = Path.Combine(_root, "Tools", "ytdlp", "2026.11.23", "yt-dlp.exe");
        Assert.Equal(path, result.Tool!.ExePath);
        Assert.Equal(exe, File.ReadAllBytes(path));
        Assert.Equal((path, "--version"), (_runs.Single().Exe, _runs.Single().Args.Single())); // tested before use
        Assert.Equal("2026.11.23", installer.GetInstalled(ToolKind.YtDlp)!.Version);
        Assert.Contains(progress, p => p.Stage == "Testing");
        Assert.False(File.Exists(Path.Combine(_root, "Tools", "ytdlp", "download.part")));
        Assert.All(_github.Requests, r => Assert.Equal("AJBrowser/1.0", r.Headers.UserAgent.ToString()));
    }

    [Fact]
    public async Task A_checksum_mismatch_installs_nothing()
    {
        ServeYtDlp("2026.11.23");
        _github.Files["https://github.com/yt-dlp/yt-dlp/releases/download/2026.11.23/yt-dlp.exe"] = Encoding.UTF8.GetBytes("tampered");

        var result = await Create().InstallAsync(ToolKind.YtDlp);

        Assert.False(result.Succeeded);
        Assert.Contains("didn't match its published checksum", result.Error);
        Assert.Null(Create().GetInstalled(ToolKind.YtDlp));
        Assert.Empty(_runs); // never run
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "Tools", "ytdlp")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "Tools", "ytdlp")));
    }

    [Fact]
    public async Task Without_an_api_digest_the_projects_checksum_file_is_used()
    {
        ServeYtDlp("2026.11.23", digest: false, checksumFile: true);
        var result = await Create().InstallAsync(ToolKind.YtDlp);
        Assert.True(result.Succeeded, result.Error);
    }

    [Fact]
    public async Task Without_any_checksum_nothing_is_installed()
    {
        ServeYtDlp("2026.11.23", digest: false);
        var result = await Create().InstallAsync(ToolKind.YtDlp);
        Assert.Contains("doesn't publish a checksum", result.Error);
        Assert.Equal(0, _github.CountFor("https://github.com/yt-dlp/yt-dlp/releases/download/2026.11.23/yt-dlp.exe")); // not even downloaded
    }

    [Fact]
    public async Task Unpacks_only_the_named_programs_from_a_zip()
    {
        var zip = Zip(
            ("ffmpeg-n9.0-latest-win64-lgpl-9.0/bin/ffmpeg.exe", "ffmpeg"),
            ("ffmpeg-n9.0-latest-win64-lgpl-9.0/bin/ffprobe.exe", "ffprobe"),
            ("ffmpeg-n9.0-latest-win64-lgpl-9.0/bin/ffplay.exe", "ffplay"),
            ("../../outside.exe", "evil"));
        const string name = "ffmpeg-n9.0-latest-win64-lgpl-9.0.zip";
        _github.Files[$"https://github.com/FFmpeg-Builds/FFmpeg-Builds/releases/download/latest/{name}"] = zip;
        _github.Add("https://api.github.com/repos/FFmpeg-Builds/FFmpeg-Builds/releases/tags/latest",
            ToolCatalogTests.ReleaseJson("FFmpeg-Builds/FFmpeg-Builds", "latest", (name, Sha(zip), zip.Length, null)));

        var result = await Create().InstallAsync(ToolKind.Ffmpeg);

        Assert.True(result.Succeeded, result.Error);
        var folder = Path.Combine(_root, "Tools", "ffmpeg", "9.0");
        Assert.Equal(new[] { "ffmpeg.exe", "ffprobe.exe" }, Directory.GetFiles(folder).Select(Path.GetFileName).Order());
        Assert.Equal("ffmpeg", File.ReadAllText(Path.Combine(folder, "ffmpeg.exe")));
        Assert.False(File.Exists(Path.Combine(_root, "outside.exe")));
        Assert.Equal("-version", _runs.Single().Args.Single());
    }

    [Fact]
    public async Task A_zip_without_the_program_is_refused()
    {
        var zip = Zip(("readme.txt", "hi"));
        _github.Files["https://github.com/denoland/deno/releases/download/v2.9.8/deno-x86_64-pc-windows-msvc.zip"] = zip;
        _github.Add("https://api.github.com/repos/denoland/deno/releases/latest",
            ToolCatalogTests.ReleaseJson("denoland/deno", "v2.9.8", ("deno-x86_64-pc-windows-msvc.zip", Sha(zip), zip.Length, null)));
        var result = await Create().InstallAsync(ToolKind.Deno);
        Assert.Contains("doesn't contain deno.exe", result.Error);
        Assert.Null(Create().GetInstalled(ToolKind.Deno));
    }

    [Fact]
    public async Task A_program_that_does_not_start_is_not_used()
    {
        ServeYtDlp("2026.11.23");
        _exitCode = -1;
        var result = await Create().InstallAsync(ToolKind.YtDlp);
        Assert.Contains("antivirus", result.Error);
        Assert.Null(Create().GetInstalled(ToolKind.YtDlp));
        Assert.False(Directory.Exists(Path.Combine(_root, "Tools", "ytdlp", "2026.11.23")));
    }

    [Fact]
    public async Task Updates_keep_the_previous_version_and_remove_older_ones()
    {
        var installer = Create();
        foreach (var tag in new[] { "2026.09.01", "2026.10.01", "2026.11.23" })
        {
            ServeYtDlp(tag);
            Assert.True((await installer.InstallAsync(ToolKind.YtDlp)).Succeeded);
            Directory.SetLastWriteTimeUtc(Path.Combine(_root, "Tools", "ytdlp", tag), DateTime.UtcNow.AddMinutes(tag[5..7] == "09" ? -20 : tag[5..7] == "10" ? -10 : 0));
        }

        Assert.Equal("2026.11.23", installer.GetInstalled(ToolKind.YtDlp)!.Version);
        var folders = Directory.GetDirectories(Path.Combine(_root, "Tools", "ytdlp")).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(new[] { "2026.10.01", "2026.11.23" }, folders);
    }

    [Fact]
    public async Task Already_up_to_date_downloads_nothing()
    {
        ServeYtDlp("2026.11.23");
        var installer = Create();
        await installer.InstallAsync(ToolKind.YtDlp);
        var again = await installer.InstallAsync(ToolKind.YtDlp);
        Assert.True(again.Succeeded);
        Assert.Equal(1, _github.CountFor("https://github.com/yt-dlp/yt-dlp/releases/download/2026.11.23/yt-dlp.exe"));
    }

    [Fact]
    public async Task Checks_for_updates_at_most_daily_and_reports_newer_releases()
    {
        ServeYtDlp("2026.10.01");
        var installer = Create();
        Assert.False(installer.IsUpdateCheckDue(ToolKind.YtDlp)); // nothing installed
        await installer.InstallAsync(ToolKind.YtDlp);
        Assert.False(installer.IsUpdateCheckDue(ToolKind.YtDlp)); // just installed = just checked

        _now = _now.AddDays(1).AddMinutes(1);
        Assert.True(installer.IsUpdateCheckDue(ToolKind.YtDlp));
        Assert.Null(await installer.CheckForUpdateAsync(ToolKind.YtDlp)); // same version
        Assert.False(installer.IsUpdateCheckDue(ToolKind.YtDlp)); // recorded

        ServeYtDlp("2026.11.23");
        var update = await installer.CheckForUpdateAsync(ToolKind.YtDlp);
        Assert.Equal("2026.11.23", update!.Version);
        Assert.Equal("2026.10.01", installer.GetInstalled(ToolKind.YtDlp)!.Version); // checking never installs
    }

    [Fact]
    public async Task Network_and_github_failures_give_a_sentence()
    {
        var result = await Create().InstallAsync(ToolKind.YtDlp); // the fake answers 404
        Assert.False(result.Succeeded);
        Assert.Contains("HTTP 404", result.Error);
        Assert.Null(await Create().CheckForUpdateAsync(ToolKind.YtDlp));
    }

    [Fact]
    public async Task Locate_prefers_settings_then_installed_then_path()
    {
        var installer = Create();
        var pathFolder = Directory.CreateDirectory(Path.Combine(_root, "onpath")).FullName;
        File.WriteAllText(Path.Combine(pathFolder, "yt-dlp.exe"), "x");
        Assert.Equal(Path.Combine(pathFolder, "yt-dlp.exe"), installer.Locate(ToolKind.YtDlp, null, pathFolder));

        ServeYtDlp("2026.11.23");
        await installer.InstallAsync(ToolKind.YtDlp);
        Assert.Equal(installer.GetInstalled(ToolKind.YtDlp)!.ExePath, installer.Locate(ToolKind.YtDlp, null, pathFolder));

        var chosen = Directory.CreateDirectory(Path.Combine(_root, "mine")).FullName;
        File.WriteAllText(Path.Combine(chosen, "yt-dlp.exe"), "x");
        Assert.Equal(Path.Combine(chosen, "yt-dlp.exe"), installer.Locate(ToolKind.YtDlp, chosen, pathFolder)); // a folder works too
        Assert.Equal(installer.GetInstalled(ToolKind.YtDlp)!.ExePath, installer.Locate(ToolKind.YtDlp, Path.Combine(_root, "gone.exe"), pathFolder));
    }

    [Fact]
    public void A_tampered_state_file_pointing_outside_the_tool_folder_is_ignored()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "Tools", "ytdlp")).FullName;
        File.WriteAllText(Path.Combine(_root, "evil.exe"), "x");
        File.WriteAllText(Path.Combine(folder, "current.json"), """{"Version":"1","Exe":"..\\..\\evil.exe","InstalledAt":"2026-01-01T00:00:00Z"}""");
        Assert.Null(Create().GetInstalled(ToolKind.YtDlp));
        File.WriteAllText(Path.Combine(folder, "current.json"), "not json");
        Assert.Null(Create().GetInstalled(ToolKind.YtDlp));
    }

    [Fact]
    public async Task The_process_runner_reports_a_missing_program_as_a_failed_run()
    {
        var result = await ProcessRunner.RunAsync(Path.Combine(_root, "missing.exe"), ["--version"]);
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("couldn't be started", result.ErrorTail.Single());
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
