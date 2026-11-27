using System.Text.Json;
using System.Text.Json.Serialization;

namespace AJBrowser.Core.Blocking;

/// <summary>
/// What kind of resource a request loads, in the filter engine's terms (filter options like <c>$script</c>,
/// <c>$image</c>, <c>$xmlhttprequest</c> match on this). The Browser layer maps WebView2's resource context to it.
/// </summary>
public enum RequestKind
{
    Document,
    Subdocument,
    Script,
    Stylesheet,
    Image,
    Media,
    Font,
    /// <summary>fetch() and XMLHttpRequest; filter lists use <c>$xmlhttprequest</c> for both.</summary>
    XmlHttpRequest,
    WebSocket,
    Ping,
    Other,
}

public static class RequestKinds
{
    /// <summary>The name adblock-rust expects for each kind.</summary>
    public static string EngineName(RequestKind kind) => kind switch
    {
        RequestKind.Document => "document",
        RequestKind.Subdocument => "subdocument",
        RequestKind.Script => "script",
        RequestKind.Stylesheet => "stylesheet",
        RequestKind.Image => "image",
        RequestKind.Media => "media",
        RequestKind.Font => "font",
        RequestKind.XmlHttpRequest => "xmlhttprequest",
        RequestKind.WebSocket => "websocket",
        RequestKind.Ping => "ping",
        _ => "other",
    };
}

/// <summary>One filter list's text, ready to load into an engine.</summary>
/// <param name="Text">The list as downloaded (Adblock Plus / uBO syntax, or a hosts file).</param>
/// <param name="ScriptletPermissions">Trust mask for privileged scriptlets (<c>trusted-*</c>); 0 = untrusted,
/// which is right for every third-party list.</param>
/// <param name="HostsFormat">True for a hosts file (<c>0.0.0.0 ads.example.com</c>).</param>
public sealed record FilterListText(string Text, byte ScriptletPermissions = 0, bool HostsFormat = false)
{
    /// <summary>Lets a list use uBO's <c>trusted-*</c> scriptlets (set by tools/ubo-resources for <c>requiresTrust</c>).
    /// Give it only to uBlock Origin's own lists, as uBO does.</summary>
    public const byte TrustedScriptlets = 1;
}

/// <summary>The engine's answer for one network request.</summary>
public readonly record struct FilterDecision(
    bool ShouldBlock,
    bool MatchedRule,
    bool MatchedException,
    bool Important,
    string? RedirectDataUrl,
    string? RewrittenUrl)
{
    /// <summary>Let the request through untouched (also the answer whenever something goes wrong).</summary>
    public static readonly FilterDecision Allow = new(false, false, false, false, null, null);
}

/// <summary>
/// What a page needs from cosmetic filtering, fetched once per document: CSS selectors to hide, the scriptlet code to
/// run before the page's own scripts, and whether generic (all-sites) hiding is allowed on it.
/// </summary>
public sealed record CosmeticResources
{
    public static readonly CosmeticResources Empty = new();

    /// <summary>Selectors hidden with <c>display: none !important</c>.</summary>
    [JsonPropertyName("hide_selectors")]
    public IReadOnlyList<string> HideSelectors { get; init; } = [];

    /// <summary>uBO procedural filters (<c>:has-text()</c>, <c>:remove()</c>…) as JSON objects; applied by the page script.</summary>
    [JsonPropertyName("procedural_actions")]
    public IReadOnlyList<string> ProceduralActions { get; init; } = [];

    /// <summary>Class/id selectors that must not be hidden generically on this page; passed back to
    /// <see cref="IFilterEngine.GetHiddenClassIdSelectors"/>.</summary>
    [JsonPropertyName("exceptions")]
    public IReadOnlyList<string> Exceptions { get; init; } = [];

    /// <summary>Scriptlet code (<c>##+js(...)</c>) to inject before page scripts; empty when none apply.</summary>
    [JsonPropertyName("injected_script")]
    public string InjectedScript { get; init; } = "";

    /// <summary>True when a <c>$generichide</c> exception turns generic hiding off for this page.</summary>
    [JsonPropertyName("generichide")]
    public bool GenericHide { get; init; }

    internal static CosmeticResources Parse(byte[] json)
    {
        if (json.Length == 0)
        {
            return Empty;
        }

        try
        {
            return JsonSerializer.Deserialize<CosmeticResources>(json) ?? Empty;
        }
        catch (JsonException)
        {
            return Empty;
        }
    }
}

/// <summary>Thrown when the native ad-block engine can't be used (DLL missing, wrong version, or it failed to build).</summary>
public sealed class FilterEngineUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
