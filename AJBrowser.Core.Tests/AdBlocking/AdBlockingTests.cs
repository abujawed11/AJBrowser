using System.IO.Compression;
using AJBrowser.Core.AdBlocking;

namespace AJBrowser.Core.Tests.AdBlocking;

public sealed class ExtensionVersionTests
{
    [Theory]
    [InlineData("2026.1124.1811", 2026, 1124, 1811, 0)]
    [InlineData("1", 1, 0, 0, 0)]
    [InlineData("1.2", 1, 2, 0, 0)]
    [InlineData("1.2.3.4", 1, 2, 3, 4)]
    [InlineData(" 0.65535 ", 0, 65535, 0, 0)]
    public void Parses_valid_versions(string text, int a, int b, int c, int d) =>
        Assert.Equal(new Version(a, b, c, d), ExtensionVersion.TryParse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1..2")]
    [InlineData("1.02")]
    [InlineData("1.65536")]
    [InlineData("1.-2")]
    [InlineData("v1.2")]
    [InlineData("1.2b")]
    [InlineData("123456")]
    public void Rejects_invalid_versions(string? text) => Assert.Null(ExtensionVersion.TryParse(text));

    [Fact]
    public void Short_and_padded_forms_compare_equal() =>
        Assert.Equal(ExtensionVersion.TryParse("1.2"), ExtensionVersion.TryParse("1.2.0.0"));

    [Fact]
    public void Compares_numerically_not_alphabetically() =>
        Assert.True(ExtensionVersion.TryParse("2026.1124.1811") > ExtensionVersion.TryParse("2026.999.9999"));

    [Theory]
    [InlineData("2026.1124.1811", "2026.1124.1811")]
    [InlineData("1.2.0.0", "1.2")]
    [InlineData("1.0.3", "1.0.3")]
    [InlineData("1.2.3.4", "1.2.3.4")]
    [InlineData("1.0.0.4", "1.0.0.4")]
    public void Formats_shortest_form(string text, string expected) =>
        Assert.Equal(expected, ExtensionVersion.Format(ExtensionVersion.TryParse(text)!));
}

public sealed class AdBlockerInstallPlannerTests
{
    private static readonly Version Bundled = new(2026, 1124, 1811, 0);

    [Fact]
    public void Installs_when_nothing_is_installed() =>
        Assert.Equal(AdBlockerInstallAction.Install, AdBlockerInstallPlanner.Decide(Bundled, false, null, false));

    [Fact]
    public void Installs_when_not_in_profile_even_if_state_remembers_one() =>
        Assert.Equal(AdBlockerInstallAction.Install, AdBlockerInstallPlanner.Decide(Bundled, false, Bundled, true));

    [Fact]
    public void Keeps_the_same_version() =>
        Assert.Equal(AdBlockerInstallAction.KeepInstalled, AdBlockerInstallPlanner.Decide(Bundled, true, Bundled, true));

    [Fact]
    public void Keeps_a_newer_installed_version() =>
        Assert.Equal(AdBlockerInstallAction.KeepInstalled, AdBlockerInstallPlanner.Decide(Bundled, true, new Version(2027, 1, 1, 0), true));

    [Fact]
    public void Replaces_an_older_version() =>
        Assert.Equal(AdBlockerInstallAction.Replace, AdBlockerInstallPlanner.Decide(Bundled, true, new Version(2026, 1001, 1, 0), true));

    [Fact]
    public void Replaces_an_unknown_version() =>
        Assert.Equal(AdBlockerInstallAction.Replace, AdBlockerInstallPlanner.Decide(Bundled, true, null, true));

    [Fact]
    public void Replaces_when_its_folder_is_gone() =>
        Assert.Equal(AdBlockerInstallAction.Replace, AdBlockerInstallPlanner.Decide(Bundled, true, Bundled, false));
}

public sealed class ExtensionPackageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AJBrowserTests", Guid.NewGuid().ToString("N"));

    public ExtensionPackageTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string MakeZip(string name, string? manifest, params (string Path, string Content)[] files)
    {
        var zip = Path.Combine(_root, name);
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        if (manifest is not null)
        {
            using var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open());
            writer.Write(manifest);
        }

        foreach (var (path, content) in files)
        {
            using var writer = new StreamWriter(archive.CreateEntry(path).Open());
            writer.Write(content);
        }

        return zip;
    }

    private static string Manifest(string version) => $$"""{ "name": "__MSG_extName__", "manifest_version": 3, "version": "{{version}}" }""";

    [Fact]
    public void Reads_the_version_from_the_manifest()
    {
        var zip = MakeZip("uBOLite_1.2.3.chromium.zip", Manifest("1.2.3"));
        Assert.Equal(new Version(1, 2, 3, 0), ExtensionPackage.TryReadVersion(zip));
    }

    [Fact]
    public void Broken_or_incomplete_zips_have_no_version()
    {
        var noManifest = MakeZip("a.zip", null, ("readme.txt", "hi"));
        var badJson = MakeZip("b.zip", "{ not json");
        var badVersion = MakeZip("c.zip", Manifest("one.two"));
        var notAZip = Path.Combine(_root, "d.zip");
        File.WriteAllText(notAZip, "definitely not a zip");

        Assert.Null(ExtensionPackage.TryReadVersion(noManifest));
        Assert.Null(ExtensionPackage.TryReadVersion(badJson));
        Assert.Null(ExtensionPackage.TryReadVersion(badVersion));
        Assert.Null(ExtensionPackage.TryReadVersion(notAZip));
        Assert.Null(ExtensionPackage.TryReadVersion(Path.Combine(_root, "missing.zip")));
    }

    [Fact]
    public void FindNewest_picks_the_highest_valid_version()
    {
        MakeZip("uBOLite_2026.900.1.chromium.zip", Manifest("2026.900.1"));
        var newest = MakeZip("uBOLite_2026.1124.1811.chromium.zip", Manifest("2026.1124.1811"));
        MakeZip("uBOLite_broken.chromium.zip", "{");
        MakeZip("other_9999.zip", Manifest("9999"));

        var package = ExtensionPackage.FindNewest(_root, "uBOLite_*.zip");

        Assert.NotNull(package);
        Assert.Equal(newest, package.ZipPath);
        Assert.Equal("2026.1124.1811", package.FolderName);
    }

    [Fact]
    public void FindNewest_returns_null_for_missing_folder_or_no_packages()
    {
        Assert.Null(ExtensionPackage.FindNewest(Path.Combine(_root, "nope"), "*.zip"));
        Assert.Null(ExtensionPackage.FindNewest(_root, "*.zip"));
    }

    [Fact]
    public void UnpackTo_extracts_into_a_version_folder_and_reuses_it()
    {
        var zip = MakeZip("x.zip", Manifest("1.5"), ("js/background.js", "// code"));
        var package = new ExtensionPackage(zip, new Version(1, 5, 0, 0));
        var parent = Path.Combine(_root, "installed");

        var folder = package.UnpackTo(parent);

        Assert.Equal(Path.Combine(parent, "1.5"), folder);
        Assert.True(File.Exists(Path.Combine(folder, "manifest.json")));
        Assert.Equal("// code", File.ReadAllText(Path.Combine(folder, "js", "background.js")));
        Assert.False(Directory.Exists(folder + ".partial"));

        // A second call reuses the existing folder instead of extracting again.
        File.WriteAllText(Path.Combine(folder, "marker"), "kept");
        Assert.Equal(folder, package.UnpackTo(parent));
        Assert.True(File.Exists(Path.Combine(folder, "marker")));
    }

    [Fact]
    public void UnpackTo_replaces_a_broken_folder_and_leftover_partial()
    {
        var zip = MakeZip("x.zip", Manifest("2.0"));
        var package = new ExtensionPackage(zip, new Version(2, 0, 0, 0));
        var parent = Path.Combine(_root, "installed");
        Directory.CreateDirectory(Path.Combine(parent, "2.0"));                 // no manifest: broken
        Directory.CreateDirectory(Path.Combine(parent, "2.0.partial", "junk")); // interrupted earlier

        var folder = package.UnpackTo(parent);

        Assert.True(File.Exists(Path.Combine(folder, "manifest.json")));
        Assert.False(Directory.Exists(Path.Combine(parent, "2.0.partial")));
    }

    [Fact]
    public void DeleteOtherVersions_removes_only_other_version_folders()
    {
        var parent = Path.Combine(_root, "installed");
        foreach (var name in new[] { "1.0", "2.0", "1.5.partial", "not-a-version" })
        {
            Directory.CreateDirectory(Path.Combine(parent, name));
        }

        File.WriteAllText(Path.Combine(parent, "state.json"), "{}");

        var failed = ExtensionPackage.DeleteOtherVersions(parent, Path.Combine(parent, "2.0"));

        Assert.Empty(failed);
        Assert.True(Directory.Exists(Path.Combine(parent, "2.0")));
        Assert.True(Directory.Exists(Path.Combine(parent, "not-a-version")));
        Assert.False(Directory.Exists(Path.Combine(parent, "1.0")));
        Assert.False(Directory.Exists(Path.Combine(parent, "1.5.partial")));
        Assert.True(File.Exists(Path.Combine(parent, "state.json")));
    }
}

public sealed class AdBlockerStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AJBrowserTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Missing_file_gives_defaults_with_blocking_on()
    {
        var state = AdBlockerState.Load(Path.Combine(_root, "state.json"));

        Assert.True(state.Enabled);
        Assert.Null(state.ExtensionId);
        Assert.Null(state.Version);
    }

    [Fact]
    public void Corrupt_file_gives_defaults()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "state.json");
        File.WriteAllText(file, "{ broken");

        Assert.Equal(new AdBlockerState(), AdBlockerState.Load(file));
    }

    [Fact]
    public void Save_then_load_round_trips()
    {
        var file = Path.Combine(_root, "sub", "state.json");
        var state = new AdBlockerState { ExtensionId = "abc", Version = "2026.1124.1811", Folder = @"C:\x\2026.1124.1811", Enabled = false };

        state.Save(file);

        Assert.Equal(state, AdBlockerState.Load(file));
        Assert.False(File.Exists(file + ".tmp"));
    }
}
