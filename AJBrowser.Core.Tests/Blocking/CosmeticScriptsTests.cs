using System.Diagnostics;
using System.Text.Json;
using AJBrowser.Core.Blocking;

namespace AJBrowser.Core.Tests.Blocking;

public sealed class CosmeticScriptsTests
{
    private const string Host = "www.news.test";

    private static CosmeticResources With(
        IReadOnlyList<string>? hide = null, string script = "", bool genericHide = false, IReadOnlyList<string>? exceptions = null) =>
        new() { HideSelectors = hide ?? [], InjectedScript = script, GenericHide = genericHide, Exceptions = exceptions ?? [] };

    // ---------------------------------------------------------------- scriptlet script

    [Fact]
    public void No_scriptlets_means_no_scriptlet_script()
    {
        Assert.Null(CosmeticScripts.BuildScriptletScript(Host, With()));
        Assert.Null(CosmeticScripts.BuildScriptletScript(Host, With(script: "  \n")));
    }

    [Fact]
    public void Scriptlets_are_wrapped_with_scriptletGlobals_and_a_host_guard()
    {
        var script = CosmeticScripts.BuildScriptletScript(Host, With(script: "setConstant(\"a\", \"1\");"))!;

        Assert.Contains("if (location.hostname !== \"www.news.test\") return;", script);
        Assert.Contains("const scriptletGlobals = {};", script);
        Assert.Contains("setConstant(\"a\", \"1\");", script);
        Assert.True(script.IndexOf("location.hostname", StringComparison.Ordinal) < script.IndexOf("setConstant", StringComparison.Ordinal));
    }

    [Fact]
    public void Host_is_written_as_a_json_string_so_it_cannot_break_out()
    {
        var script = CosmeticScripts.BuildScriptletScript("a\"b</script>", With(script: "x();"))!;
        Assert.Contains("\"a\\u0022b\\u003C/script\\u003E\"", script);
    }

    // ---------------------------------------------------------------- cosmetic script

    [Fact]
    public void Nothing_to_hide_and_generic_hiding_off_means_no_script()
    {
        Assert.Null(CosmeticScripts.BuildCosmeticScript(Host, With(genericHide: true)));
    }

    [Fact]
    public void Hide_selectors_become_one_rule_each()
    {
        var script = CosmeticScripts.BuildCosmeticScript(Host, With(hide: [".ad", "#banner", "div[data-ad=\"x\"]"], genericHide: true))!;

        var css = ExtractCss(script);
        Assert.Equal(".ad{display:none!important}\n#banner{display:none!important}\ndiv[data-ad=\"x\"]{display:none!important}\n", css);
        Assert.DoesNotContain("MutationObserver((records)", script); // generic observer is off
    }

    [Fact]
    public void Generic_observer_is_included_unless_generichide()
    {
        var script = CosmeticScripts.BuildCosmeticScript(Host, With())!;
        Assert.Contains("'ajb-cosmetic'", script);
        Assert.Contains("'ajb-cosmetic-css'", script);
        Assert.Contains("MutationObserver((records)", script);
        Assert.DoesNotContain("addCss(\"", script); // no site selectors
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".a{}")]
    [InlineData(".a}body{color:red")]
    [InlineData(".a /* x")]
    [InlineData(".a\n.b")]
    [InlineData(".a\\")]
    public void Unsafe_selectors_are_dropped(string selector)
    {
        Assert.Empty(CosmeticScripts.CleanSelectors([selector], 10));
    }

    [Fact]
    public void Selectors_are_trimmed_deduplicated_capped_and_long_ones_dropped()
    {
        var result = CosmeticScripts.CleanSelectors([" .a ", ".a", ".b", new string('x', CosmeticScripts.MaxSelectorLength + 1), ".c", ".d"], 3);
        Assert.Equal([".a", ".b", ".c"], result);
    }

    // ---------------------------------------------------------------- messages

    [Fact]
    public void Parses_a_well_formed_request()
    {
        Assert.True(CosmeticScripts.TryParseRequest("""{"type":"ajb-cosmetic","classes":["ad","x","ad"],"ids":["top"]}""", out var classes, out var ids));
        Assert.Equal(["ad", "x"], classes);
        Assert.Equal(["top"], ids);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"classes":["ad"]}""")]
    [InlineData("""{"type":"ajb-video","classes":["ad"]}""")]
    [InlineData("""{"type":"ajb-cosmetic","classes":[],"ids":[]}""")]
    [InlineData("""{"type":"ajb-cosmetic","classes":"ad"}""")]
    [InlineData("""{"type":7,"classes":["ad"]}""")]
    public void Rejects_anything_else(string? message)
    {
        Assert.False(CosmeticScripts.TryParseRequest(message, out _, out _));
    }

    [Fact]
    public void Odd_entries_are_skipped_and_counts_capped()
    {
        var names = Enumerable.Range(0, CosmeticScripts.MaxNamesPerMessage + 50).Select(i => $"\"c{i}\"");
        var json = $$"""{"type":"ajb-cosmetic","classes":[1,null,"","{{new string('x', CosmeticScripts.MaxNameLength + 1)}}",{{string.Join(",", names)}}],"ids":[{}]}""";

        Assert.True(CosmeticScripts.TryParseRequest(json, out var classes, out var ids));
        Assert.Equal(CosmeticScripts.MaxNamesPerMessage, classes.Count);
        Assert.Equal("c0", classes[0]);
        Assert.Empty(ids);
    }

    [Fact]
    public void Oversized_messages_are_refused_unread()
    {
        var json = "{\"type\":\"ajb-cosmetic\",\"classes\":[\"a\"],\"pad\":\"" + new string('x', CosmeticScripts.MaxMessageLength) + "\"}";
        Assert.False(CosmeticScripts.TryParseRequest(json, out _, out _));
    }

    [Fact]
    public void Reply_carries_clean_selectors_or_nothing()
    {
        Assert.Null(CosmeticScripts.BuildReply([]));
        Assert.Null(CosmeticScripts.BuildReply(["a{}"]));

        using var doc = JsonDocument.Parse(CosmeticScripts.BuildReply([".ad", ".ad", "#x"])!);
        Assert.Equal("ajb-cosmetic-css", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal([".ad", "#x"], doc.RootElement.GetProperty("selectors").EnumerateArray().Select(e => e.GetString()!));
    }

    // ---------------------------------------------------------------- with the real engine and uBO's scriptlets

    [Fact]
    public void Real_engine_output_produces_scripts_for_the_site()
    {
        var resources = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "ubo-resources.json"));
        using var engine = AdblockRustEngine.Build(
            [new FilterListText("news.test##.sponsored\nnews.test##+js(set-constant, adsEnabled, false)\n##.ad-banner")],
            resources);

        var cosmetic = engine.GetCosmeticResources("https://www.news.test/article");
        var scriptlets = CosmeticScripts.BuildScriptletScript(Host, cosmetic)!;
        var hiding = CosmeticScripts.BuildCosmeticScript(Host, cosmetic)!;

        Assert.Contains("setConstant(\"adsEnabled\", \"false\")", scriptlets);
        Assert.Contains(".sponsored{display:none!important}", ExtractCss(hiding));
        Assert.Contains("ajb-cosmetic", hiding); // generic rules exist, so the observer is there

        var reply = CosmeticScripts.BuildReply(engine.GetHiddenClassIdSelectors(["ad-banner", "content"], [], cosmetic.Exceptions));
        Assert.Contains(".ad-banner", reply);

        AssertParsesInNode(scriptlets);
        AssertParsesInNode(hiding);
    }

    /// <summary>The CSS argument of the script's addCss("…") call.</summary>
    private static string ExtractCss(string script)
    {
        var start = script.IndexOf("addCss(\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "no addCss call");
        // The argument is one JSON string followed by ");": read just that token.
        var json = script[(start + 7)..];
        var end = json.IndexOf("\");", StringComparison.Ordinal);
        return JsonSerializer.Deserialize<string>(json[..(end + 1)])!;
    }

    /// <summary>Syntax check with Node when it's installed (the developer PC has it; skipped quietly otherwise).</summary>
    private static void AssertParsesInNode(string script)
    {
        var file = Path.Combine(Path.GetTempPath(), $"ajb-script-{Guid.NewGuid():N}.js");
        File.WriteAllText(file, script);
        try
        {
            using var node = Process.Start(new ProcessStartInfo("node", ["--check", file])
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });
            if (node is null)
            {
                return;
            }

            var errors = node.StandardError.ReadToEnd();
            node.WaitForExit(30_000);
            Assert.True(node.ExitCode == 0, errors);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Node isn't installed: nothing to check with.
        }
        finally
        {
            File.Delete(file);
        }
    }
}
