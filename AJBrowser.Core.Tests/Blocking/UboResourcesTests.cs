using AJBrowser.Core.Blocking;

namespace AJBrowser.Core.Tests.Blocking;

/// <summary>
/// Checks the scriptlet/redirect file the app ships (AJBrowser/Filters/ubo-resources.json, generated from a uBO release
/// by tools/ubo-resources/generate.mjs) against the real engine. Re-run after regenerating that file.
/// </summary>
public sealed class UboResourcesTests
{
    private const string News = "https://news.test/article";

    private static readonly Lazy<string> Resources = new(() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "ubo-resources.json")));

    private static AdblockRustEngine Engine(string list, byte permissions = 0) =>
        AdblockRustEngine.Build([new FilterListText(list, permissions)], Resources.Value);

    [Fact]
    public void Shipped_file_is_accepted_by_the_engine()
    {
        using var engine = Engine("||ads.example^");
        Assert.True(engine.HasResources);
    }

    [Fact]
    public void Scriptlet_is_called_with_its_arguments_and_helpers_are_included()
    {
        using var engine = Engine("news.test##+js(set-constant, adsEnabled, false)");
        var script = engine.GetCosmeticResources(News).InjectedScript;
        Assert.Contains("""setConstant("adsEnabled", "false")""", script);
        Assert.Contains("function setConstantFn(", script);
        Assert.Contains("function safeSelf(", script);
    }

    [Fact]
    public void Short_aliases_work()
    {
        using var engine = Engine("news.test##+js(aopr, adblockDetector)");
        Assert.Contains("""abortOnPropertyRead("adblockDetector")""", engine.GetCosmeticResources(News).InjectedScript);
    }

    [Fact]
    public void Trusted_scriptlets_run_only_for_trusted_lists()
    {
        const string rule = "news.test##+js(trusted-set-constant, ytConfig.ads, {})";
        using (var untrusted = Engine(rule))
        {
            Assert.Equal("", untrusted.GetCosmeticResources(News).InjectedScript);
        }

        using var trusted = Engine(rule, FilterListText.TrustedScriptlets);
        Assert.Contains("trustedSetConstant(", trusted.GetCosmeticResources(News).InjectedScript);
    }

    [Fact]
    public void Redirect_files_are_served_as_data_urls()
    {
        using var engine = Engine("||ads.example/ad.js$script,redirect=noop.js\n||ads.example/px.gif$image,redirect=1x1.gif");
        Assert.StartsWith("data:application/javascript;base64,",
            engine.Check("https://ads.example/ad.js", News, RequestKind.Script).RedirectDataUrl);
        Assert.StartsWith("data:image/gif;base64,",
            engine.Check("https://ads.example/px.gif", News, RequestKind.Image).RedirectDataUrl);
    }
}
