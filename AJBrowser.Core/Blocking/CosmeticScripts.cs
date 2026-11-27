using System.Text;
using System.Text.Json;

namespace AJBrowser.Core.Blocking;

/// <summary>
/// Builds the scripts AJBrowser's own engine injects into pages (Phase 8c) and checks the messages they send back.
/// <list type="bullet">
/// <item><b>Scriptlet script:</b> uBO's scriptlets for one site (what removes YouTube's ads), wrapped the way uBO runs
/// them. Kept separate from the cosmetic script so a scriptlet that doesn't parse can't take the hiding down with it.</item>
/// <item><b>Cosmetic script:</b> the site's hide selectors as a constructed stylesheet, plus (unless the site has a
/// <c>$generichide</c> exception) a <c>MutationObserver</c> that sends the page's class names and ids to AJBrowser in
/// batches; the answer is more selectors to hide.</item>
/// </list>
/// Scripts are registered per host (WebView2 can't register a script for one address only), so each one starts by
/// checking <c>location.hostname</c> and does nothing on other sites.
/// <para>Messages from pages are untrusted: <see cref="TryParseRequest"/> caps sizes and drops anything odd.</para>
/// </summary>
public static class CosmeticScripts
{
    /// <summary>Page → AJBrowser: <c>{"type":"ajb-cosmetic","classes":[…],"ids":[…]}</c>.</summary>
    public const string RequestType = "ajb-cosmetic";

    /// <summary>AJBrowser → page: <c>{"type":"ajb-cosmetic-css","selectors":[…]}</c>.</summary>
    public const string ReplyType = "ajb-cosmetic-css";

    /// <summary>Most class names (and, separately, ids) in one message; the page script sends more in later batches.</summary>
    public const int MaxNamesPerMessage = 1000;

    /// <summary>Longer class names or ids are ignored (no filter rule targets them).</summary>
    public const int MaxNameLength = 256;

    /// <summary>Longest message read from a page (characters).</summary>
    public const int MaxMessageLength = 300_000;

    /// <summary>Longest selector put into a stylesheet; longer ones are dropped.</summary>
    public const int MaxSelectorLength = 4096;

    /// <summary>Most selectors in one reply.</summary>
    public const int MaxSelectorsPerReply = 2000;

    /// <summary>Delay before the page script sends a batch (collects the names of a burst of DOM changes).</summary>
    public const int BatchDelayMilliseconds = 100;

    /// <summary>Most names one document sends in total (heavy single-page apps keep creating new ones).</summary>
    public const int MaxNamesPerDocument = 20_000;

    /// <summary>
    /// The scriptlet code for <paramref name="host"/>, or null when there is none. uBO's scriptlets read a
    /// <c>scriptletGlobals</c> object (8a finding), so the engine's code runs inside a function that defines it.
    /// </summary>
    public static string? BuildScriptletScript(string host, CosmeticResources resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        if (string.IsNullOrWhiteSpace(resources.InjectedScript))
        {
            return null;
        }

        var sb = new StringBuilder(resources.InjectedScript.Length + 256);
        sb.Append("(function(){\n");
        sb.Append("if (location.hostname !== ").Append(JsonString(host)).Append(") return;\n");
        sb.Append("try {\n(function(){\nconst scriptletGlobals = {};\n");
        sb.Append(resources.InjectedScript);
        sb.Append("\n})();\n} catch (e) { }\n})();\n");
        return sb.ToString();
    }

    /// <summary>
    /// The hiding script for <paramref name="host"/>, or null when it would do nothing (no selectors and generic
    /// hiding is off for the site).
    /// </summary>
    public static string? BuildCosmeticScript(string host, CosmeticResources resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var selectors = CleanSelectors(resources.HideSelectors, int.MaxValue);
        var generic = !resources.GenericHide;
        if (selectors.Count == 0 && !generic)
        {
            return null;
        }

        var sb = new StringBuilder(1024 + selectors.Sum(s => s.Length + 32));
        sb.Append("(function(){\n");
        sb.Append("if (location.hostname !== ").Append(JsonString(host)).Append(") return;\n");
        sb.Append(AddCssFunction);
        if (selectors.Count > 0)
        {
            sb.Append("addCss(").Append(JsonString(ToCss(selectors))).Append(");\n");
        }

        if (generic)
        {
            sb.Append(GenericObserver);
        }

        sb.Append("})();\n");
        return sb.ToString();
    }

    /// <summary>Reads a page's class/id batch. False for anything that isn't a well-formed request.</summary>
    public static bool TryParseRequest(string? message, out IReadOnlyList<string> classes, out IReadOnlyList<string> ids)
    {
        classes = [];
        ids = [];
        if (string.IsNullOrEmpty(message) || message.Length > MaxMessageLength)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || type.GetString() != RequestType)
            {
                return false;
            }

            classes = ReadNames(root, "classes");
            ids = ReadNames(root, "ids");
            return classes.Count > 0 || ids.Count > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The reply for a batch, or null when there is nothing to hide.</summary>
    public static string? BuildReply(IEnumerable<string> selectors)
    {
        var clean = CleanSelectors(selectors, MaxSelectorsPerReply);
        return clean.Count == 0 ? null : JsonSerializer.Serialize(new { type = ReplyType, selectors = clean });
    }

    /// <summary>
    /// Selectors safe to put in a stylesheet one rule each: not empty, not too long, no braces or comment markers
    /// that could end the rule early, no duplicates. (A broken selector only drops its own rule.)
    /// </summary>
    public static IReadOnlyList<string> CleanSelectors(IEnumerable<string>? selectors, int max)
    {
        if (selectors is null)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var raw in selectors)
        {
            if (result.Count >= max)
            {
                break;
            }

            var s = raw?.Trim();
            if (string.IsNullOrEmpty(s) || s.Length > MaxSelectorLength
                || s.AsSpan().IndexOfAny("{}\n\r") >= 0 || s.Contains("/*", StringComparison.Ordinal)
                || s.Contains('\\') && s.EndsWith('\\'))
            {
                continue;
            }

            if (seen.Add(s))
            {
                result.Add(s);
            }
        }

        return result;
    }

    private static string ToCss(IReadOnlyList<string> selectors)
    {
        var sb = new StringBuilder();
        foreach (var s in selectors)
        {
            sb.Append(s).Append("{display:none!important}\n");
        }

        return sb.ToString();
    }

    private static IReadOnlyList<string> ReadNames(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            if (result.Count >= MaxNamesPerMessage)
            {
                break;
            }

            if (item.ValueKind == JsonValueKind.String
                && item.GetString() is { Length: > 0 and <= MaxNameLength } name
                && seen.Add(name))
            {
                result.Add(name);
            }
        }

        return result;
    }

    private static string JsonString(string value) => JsonSerializer.Serialize(value);

    /// <summary>Adds CSS as a constructed stylesheet (works before &lt;html&gt; exists and isn't part of the DOM);
    /// falls back to a &lt;style&gt; element once the document has a root.</summary>
    private const string AddCssFunction = """
        const addCss = (css) => {
          try {
            const sheet = new CSSStyleSheet();
            sheet.replaceSync(css);
            document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet];
            return;
          } catch (e) { }
          const insert = () => {
            const root = document.head || document.documentElement;
            if (!root) return false;
            const style = document.createElement('style');
            style.textContent = css;
            root.appendChild(style);
            return true;
          };
          if (!insert()) {
            const mo = new MutationObserver(() => { if (insert()) mo.disconnect(); });
            mo.observe(document, { childList: true, subtree: true });
          }
        };

        """;

    /// <summary>Collects class names and ids as the page builds its DOM and asks AJBrowser which of them to hide.</summary>
    private static readonly string GenericObserver = $$"""
        const wv = window.chrome && window.chrome.webview;
        if (wv && typeof wv.postMessage === 'function') {
          const MAXN = {{MaxNamesPerMessage}}, MAXL = {{MaxNameLength}}, MAXTOTAL = {{MaxNamesPerDocument}};
          const seenC = new Set(), seenI = new Set();
          const pendC = [], pendI = [];
          let total = 0, timer = 0;
          const flush = () => {
            timer = 0;
            if (pendC.length === 0 && pendI.length === 0) return;
            const classes = pendC.splice(0, MAXN), ids = pendI.splice(0, MAXN);
            try { wv.postMessage(JSON.stringify({ type: '{{RequestType}}', classes, ids })); } catch (e) { }
            if (pendC.length || pendI.length) schedule();
          };
          const schedule = () => { if (!timer) timer = setTimeout(flush, {{BatchDelayMilliseconds}}); };
          const addEl = (el) => {
            if (total >= MAXTOTAL) return;
            const id = el.id;
            if (typeof id === 'string' && id.length > 0 && id.length <= MAXL && !seenI.has(id)) { seenI.add(id); pendI.push(id); total++; }
            const cl = el.classList;
            if (cl) for (const c of cl) {
              if (c.length <= MAXL && !seenC.has(c)) { seenC.add(c); pendC.push(c); total++; }
            }
          };
          const scan = (node) => {
            if (node.nodeType !== 1) return;
            addEl(node);
            if (node.firstElementChild) for (const el of node.querySelectorAll('[id],[class]')) addEl(el);
          };
          new MutationObserver((records) => {
            if (total >= MAXTOTAL) return;
            for (const r of records) {
              if (r.type === 'attributes') addEl(r.target);
              else for (const n of r.addedNodes) scan(n);
            }
            if (pendC.length || pendI.length) schedule();
          }).observe(document, { childList: true, subtree: true, attributes: true, attributeFilter: ['class', 'id'] });
          wv.addEventListener('message', (ev) => {
            const d = ev.data;
            if (!d || d.type !== '{{ReplyType}}' || !Array.isArray(d.selectors) || d.selectors.length === 0) return;
            addCss(d.selectors.map((s) => String(s) + '{display:none!important}').join('\n'));
          });
        }

        """;
}
