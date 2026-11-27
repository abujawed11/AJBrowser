using System.Text;
using AJBrowser.Core.Blocking;

namespace AJBrowser.Core.Tests.Blocking;

public sealed class AdblockRustEngineTests
{
    private const string News = "https://news.test/article";

    private static AdblockRustEngine Engine(string list, string? resources = null) =>
        AdblockRustEngine.Build([new FilterListText(list)], resources);

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    /// <summary>A template scriptlet (like uBO's) and a redirect body, in adblock-rust's resources.json format.</summary>
    private static readonly string Resources = $$"""
        [
          {"name":"hello.js","aliases":["hi.js"],"kind":"template","content":"{{Base64("window.__ajbHello = '{{1}}';")}}"},
          {"name":"noop.js","aliases":[],"kind":{"mime":"application/javascript"},"content":"{{Base64("(function(){})();")}}"}
        ]
        """;

    [Fact]
    public void Native_library_loads() => Assert.Null(AdblockRustEngine.UnavailableReason);

    [Fact]
    public void Blocks_a_listed_domain_and_allows_others()
    {
        using var engine = Engine("||ads.example^");
        Assert.True(engine.Check("https://ads.example/banner.js", News, RequestKind.Script).ShouldBlock);
        Assert.True(engine.Check("https://sub.ads.example/x.gif", News, RequestKind.Image).ShouldBlock);
        Assert.False(engine.Check("https://news.test/app.js", News, RequestKind.Script).ShouldBlock);
        Assert.False(engine.Check("https://notads.example/x.js", News, RequestKind.Script).ShouldBlock);
    }

    [Fact]
    public void Exception_rule_allows_a_matched_request()
    {
        using var engine = Engine("||ads.example^\n@@||ads.example/allowed.js");
        var decision = engine.Check("https://ads.example/allowed.js", News, RequestKind.Script);
        Assert.False(decision.ShouldBlock);
        Assert.True(decision.MatchedRule);
        Assert.True(decision.MatchedException);
    }

    [Fact]
    public void Important_rule_beats_an_exception()
    {
        using var engine = Engine("||ads.example^$important\n@@||ads.example^");
        var decision = engine.Check("https://ads.example/a.js", News, RequestKind.Script);
        Assert.True(decision.ShouldBlock);
        Assert.True(decision.Important);
    }

    [Fact]
    public void Third_party_option_only_blocks_requests_from_other_sites()
    {
        using var engine = Engine("||cdn.test^$third-party");
        Assert.True(engine.Check("https://cdn.test/t.js", News, RequestKind.Script).ShouldBlock);
        Assert.False(engine.Check("https://cdn.test/t.js", "https://www.cdn.test/", RequestKind.Script).ShouldBlock);
    }

    [Fact]
    public void Domain_option_limits_a_rule_to_listed_pages()
    {
        using var engine = Engine("||widget.test^$domain=news.test");
        Assert.True(engine.Check("https://widget.test/w.js", News, RequestKind.Script).ShouldBlock);
        Assert.False(engine.Check("https://widget.test/w.js", "https://blog.test/", RequestKind.Script).ShouldBlock);
    }

    [Fact]
    public void Type_options_match_the_request_kind()
    {
        using var engine = Engine("||img.test^$image\n||api.test^$xmlhttprequest");
        Assert.True(engine.Check("https://img.test/a.png", News, RequestKind.Image).ShouldBlock);
        Assert.False(engine.Check("https://img.test/a.js", News, RequestKind.Script).ShouldBlock);
        Assert.True(engine.Check("https://api.test/track", News, RequestKind.XmlHttpRequest).ShouldBlock);
        Assert.False(engine.Check("https://api.test/track", News, RequestKind.Document).ShouldBlock);
    }

    [Fact]
    public void Hosts_file_format_blocks_listed_hosts()
    {
        using var engine = AdblockRustEngine.Build([new FilterListText("# comment\n0.0.0.0 tracker.test\n", HostsFormat: true)]);
        Assert.True(engine.Check("https://tracker.test/p", News, RequestKind.Image).ShouldBlock);
        Assert.False(engine.Check("https://news.test/p", News, RequestKind.Image).ShouldBlock);
    }

    [Fact]
    public void Several_lists_combine()
    {
        using var engine = AdblockRustEngine.Build([new FilterListText("||one.test^"), new FilterListText("||two.test^")]);
        Assert.True(engine.Check("https://one.test/", News, RequestKind.Script).ShouldBlock);
        Assert.True(engine.Check("https://two.test/", News, RequestKind.Script).ShouldBlock);
    }

    [Fact]
    public void Redirect_rule_returns_the_replacement_as_a_data_url()
    {
        using var engine = Engine("||ads.example/ad.js$script,redirect=noop.js", Resources);
        var decision = engine.Check("https://ads.example/ad.js", News, RequestKind.Script);
        Assert.True(decision.ShouldBlock);
        Assert.StartsWith("data:application/javascript;base64,", decision.RedirectDataUrl);
    }

    [Fact]
    public void Site_specific_hiding_rules_apply_to_their_site_only()
    {
        using var engine = Engine("news.test##.sponsored\nnews.test##div#promo");
        Assert.Equal([".sponsored", "div#promo"], engine.GetCosmeticResources(News).HideSelectors.Order(StringComparer.Ordinal));
        Assert.Empty(engine.GetCosmeticResources("https://blog.test/").HideSelectors);
    }

    [Fact]
    public void Generic_hiding_rules_come_from_class_and_id_lookups()
    {
        using var engine = Engine("##.ad-banner\n###top-ad");
        Assert.Equal([".ad-banner"], engine.GetHiddenClassIdSelectors(["ad-banner", "content"], [], []));
        Assert.Equal(["#top-ad"], engine.GetHiddenClassIdSelectors([], ["top-ad"], []));
        Assert.Empty(engine.GetHiddenClassIdSelectors(["content"], ["main"], []));
    }

    [Fact]
    public void Hiding_exceptions_are_reported_and_respected()
    {
        using var engine = Engine("##.ad-banner\nnews.test#@#.ad-banner");
        var resources = engine.GetCosmeticResources(News);
        Assert.Contains(".ad-banner", resources.Exceptions);
        Assert.Empty(engine.GetHiddenClassIdSelectors(["ad-banner"], [], resources.Exceptions));
    }

    [Fact]
    public void Generichide_exception_is_reported()
    {
        using var engine = Engine("##.ad-banner\n@@||news.test^$generichide");
        Assert.True(engine.GetCosmeticResources(News).GenericHide);
        Assert.False(engine.GetCosmeticResources("https://blog.test/").GenericHide);
    }

    [Fact]
    public void Scriptlet_rule_produces_injected_code_when_resources_are_loaded()
    {
        using var engine = Engine("news.test##+js(hi, world)", Resources);
        Assert.True(engine.HasResources);
        var script = engine.GetCosmeticResources(News).InjectedScript;
        Assert.Contains("window.__ajbHello = 'world';", script);
        Assert.Equal("", engine.GetCosmeticResources("https://blog.test/").InjectedScript);
    }

    [Fact]
    public void Without_resources_scriptlets_do_nothing_but_blocking_works()
    {
        using var engine = Engine("news.test##+js(hi, world)\n||ads.example^");
        Assert.False(engine.HasResources);
        Assert.Equal("", engine.GetCosmeticResources(News).InjectedScript);
        Assert.True(engine.Check("https://ads.example/a.js", News, RequestKind.Script).ShouldBlock);
    }

    [Fact]
    public void Broken_resources_file_keeps_the_engine_usable()
    {
        using var engine = Engine("||ads.example^", "{ not json");
        Assert.False(engine.HasResources);
        Assert.True(engine.Check("https://ads.example/a.js", News, RequestKind.Script).ShouldBlock);
    }

    [Fact]
    public void Serialized_engine_loads_back_with_the_same_rules()
    {
        byte[] saved;
        using (var engine = Engine("||ads.example^\nnews.test##.sponsored\nnews.test##+js(hi, again)"))
        {
            saved = engine.Serialize();
        }

        Assert.NotEmpty(saved);
        using var loaded = AdblockRustEngine.TryDeserialize(saved, Resources);
        Assert.NotNull(loaded);
        Assert.True(loaded.Check("https://ads.example/a.js", News, RequestKind.Script).ShouldBlock);
        Assert.Contains(".sponsored", loaded.GetCosmeticResources(News).HideSelectors);
        Assert.Contains("window.__ajbHello = 'again';", loaded.GetCosmeticResources(News).InjectedScript);
    }

    [Fact]
    public void Corrupt_or_empty_saved_data_is_refused()
    {
        byte[] saved;
        using (var engine = Engine("||ads.example^"))
        {
            saved = engine.Serialize();
        }

        Assert.Null(AdblockRustEngine.TryDeserialize([]));
        Assert.Null(AdblockRustEngine.TryDeserialize("not an engine"u8));
        Assert.Null(AdblockRustEngine.TryDeserialize(saved.AsSpan(0, saved.Length / 2)));
        saved[^10] ^= 0xFF;
        Assert.Null(AdblockRustEngine.TryDeserialize(saved));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("data:text/javascript,alert(1)")]
    [InlineData("blob:https://ads.example/123")]
    [InlineData("chrome-extension://abc/x.js")]
    public void Odd_addresses_are_allowed_without_errors(string url)
    {
        using var engine = Engine("||ads.example^\n*");
        Assert.Equal(FilterDecision.Allow, engine.Check(url, News, RequestKind.Script));
    }

    [Fact]
    public void Long_and_non_ascii_addresses_work()
    {
        using var engine = Engine("||ads.example^\n||xn--bcher-kva.test^");
        var longUrl = "https://ads.example/" + new string('a', 10_000);
        Assert.True(engine.Check(longUrl, News, RequestKind.Script).ShouldBlock);
        Assert.True(engine.Check("https://bücher.test/ü.js", "https://seite.test/ä", RequestKind.Script).ShouldBlock);
    }

    [Fact]
    public void Checks_after_dispose_allow_instead_of_crashing()
    {
        var engine = Engine("||ads.example^\nnews.test##.x");
        engine.Dispose();
        Assert.Equal(FilterDecision.Allow, engine.Check("https://ads.example/a.js", News, RequestKind.Script));
        Assert.Same(CosmeticResources.Empty, engine.GetCosmeticResources(News));
        Assert.Empty(engine.GetHiddenClassIdSelectors(["x"], [], []));
        engine.Dispose(); // twice is fine
    }

    [Fact]
    public void Checks_from_many_threads_at_once_agree()
    {
        using var engine = Engine("||ads.example^\n@@||ads.example/ok.js");
        var results = new bool[2000];
        Parallel.For(0, results.Length, i =>
        {
            var url = i % 2 == 0 ? $"https://ads.example/{i}.js" : "https://ads.example/ok.js";
            results[i] = engine.Check(url, News, RequestKind.Script).ShouldBlock;
        });

        for (var i = 0; i < results.Length; i++)
        {
            Assert.Equal(i % 2 == 0, results[i]);
        }
    }

    [Fact]
    public void Every_request_kind_has_an_engine_name()
    {
        foreach (var kind in Enum.GetValues<RequestKind>())
        {
            Assert.False(string.IsNullOrEmpty(RequestKinds.EngineName(kind)));
        }

        Assert.Equal("xmlhttprequest", RequestKinds.EngineName(RequestKind.XmlHttpRequest));
        Assert.Equal("subdocument", RequestKinds.EngineName(RequestKind.Subdocument));
    }
}
