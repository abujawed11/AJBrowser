using AJBrowser.Core.Blocking;
using AJBrowser.Core.Persistence;
using AJBrowser.Core.Tests.Persistence;

namespace AJBrowser.Core.Tests.Blocking;

public sealed class BlockingPolicyTests : DatabaseTestBase
{
    private BlockingPolicy Create() => new(new SiteSettingsRepository(CreateInitialized()));

    [Theory]
    [InlineData("https://www.Example.com/page?q=1", "example.com")]
    [InlineData("http://news.example.com:8080/", "news.example.com")]
    [InlineData("example.com", "example.com")]
    [InlineData("WWW.example.com", "example.com")]
    [InlineData("https://bücher.test/", "xn--bcher-kva.test")]
    [InlineData("https://192.168.1.1/admin", "192.168.1.1")]
    [InlineData("https://example.com./", "example.com")]
    [InlineData("https://www./", "www")]
    [InlineData("ajbrowser://settings", null)]
    [InlineData("file:///C:/page.html", null)]
    [InlineData("data:text/html,hi", null)]
    [InlineData("not a host", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Site_keys(string? input, string? expected) => Assert.Equal(expected, BlockingPolicy.SiteKey(input));

    [Fact]
    public async Task Turning_a_site_off_covers_its_subdomains_only()
    {
        var policy = Create();
        await policy.LoadAsync();
        Assert.True(policy.IsEnabledFor("https://news.example.com/a"));

        await policy.SetSiteEnabledAsync("https://www.example.com/", enabled: false);

        Assert.False(policy.IsEnabledFor("https://example.com/"));
        Assert.False(policy.IsEnabledFor("https://news.example.com/a"));
        Assert.True(policy.IsEnabledFor("https://notexample.com/"));
        Assert.True(policy.IsEnabledFor("https://example.com.evil.test/"));
        Assert.Equal(["example.com"], policy.DisabledSites);
    }

    [Fact]
    public async Task Turning_blocking_back_on_for_a_subdomain_removes_the_parent_entry()
    {
        var policy = Create();
        await policy.SetSiteEnabledAsync("example.com", enabled: false);
        await policy.SetSiteEnabledAsync("sub.example.com", enabled: false);

        await policy.SetSiteEnabledAsync("https://sub.example.com/", enabled: true);

        Assert.True(policy.IsEnabledFor("https://sub.example.com/"));
        Assert.True(policy.IsEnabledFor("https://example.com/"));
        Assert.Empty(policy.DisabledSites);
    }

    [Fact]
    public async Task Site_choices_survive_a_restart()
    {
        var first = Create();
        await first.SetSiteEnabledAsync("https://a.test/", enabled: false);
        await first.SetSiteEnabledAsync("https://b.test/", enabled: false);
        await first.SetSiteEnabledAsync("https://b.test/", enabled: true);

        var second = new BlockingPolicy(new SiteSettingsRepository(new Database(DbFile)));
        await second.LoadAsync();

        Assert.Equal(["a.test"], second.DisabledSites);
        Assert.False(second.IsEnabledFor("https://a.test/"));
    }

    [Fact]
    public async Task Global_switch_overrides_everything_and_raises_changed()
    {
        var policy = Create();
        var changes = 0;
        policy.Changed += (_, _) => changes++;

        policy.Enabled = false;
        policy.Enabled = false; // no change, no event

        Assert.False(policy.IsEnabledFor("https://a.test/"));
        Assert.False(policy.IsSiteDisabled("https://a.test/"));
        Assert.Equal(1, changes);

        await policy.SetSiteEnabledAsync("a.test", enabled: false);
        Assert.Equal(2, changes);
        Assert.True(policy.IsSiteDisabled("https://a.test/"));
    }

    [Fact]
    public async Task Non_web_pages_are_never_blocked_and_cant_be_listed()
    {
        var policy = Create();
        Assert.False(policy.IsEnabledFor("ajbrowser://history"));
        Assert.False(await policy.SetSiteEnabledAsync("about:blank", enabled: false));
        Assert.Empty(policy.DisabledSites);
    }
}
