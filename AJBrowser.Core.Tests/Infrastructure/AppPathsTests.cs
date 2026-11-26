using AJBrowser.Core.Infrastructure;

namespace AJBrowser.Core.Tests.Infrastructure;

public sealed class AppPathsTests : IDisposable
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
    public void Paths_are_laid_out_under_the_root()
    {
        var paths = new AppPaths(_root);

        Assert.Equal(Path.Combine(_root, "WebView2"), paths.WebView2UserData);
        Assert.Equal(Path.Combine(_root, "Data", "ajbrowser.db"), paths.DatabaseFile);
        Assert.Equal(Path.Combine(_root, "Data", "settings.json"), paths.SettingsFile);
        Assert.Equal(Path.Combine(_root, "Filters"), paths.Filters);
        Assert.Equal(Path.Combine(_root, "Logs"), paths.Logs);
        Assert.Equal(Path.Combine(_root, "Temp"), paths.Temp);
        Assert.Equal(Path.Combine(_root, "Tools"), paths.Tools);
    }

    [Fact]
    public void EnsureCreated_creates_every_directory_and_is_idempotent()
    {
        var paths = new AppPaths(_root);

        paths.EnsureCreated();
        paths.EnsureCreated();

        Assert.All(paths.AllDirectories, d => Assert.True(Directory.Exists(d), d));
    }

    [Fact]
    public void Default_root_is_in_local_app_data()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppPaths.AppFolderName);

        Assert.Equal(expected, AppPaths.CreateDefault().Root);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_root_is_rejected(string root)
    {
        Assert.ThrowsAny<ArgumentException>(() => new AppPaths(root));
    }
}
