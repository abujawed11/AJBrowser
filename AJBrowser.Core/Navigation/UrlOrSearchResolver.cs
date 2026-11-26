using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace AJBrowser.Core.Navigation;

public enum AddressKind
{
    /// <summary>Nothing to do (empty input).</summary>
    Empty,

    /// <summary>The input was an address; navigate to <see cref="AddressResolution.Uri"/>.</summary>
    Url,

    /// <summary>The input was search text; <see cref="AddressResolution.Uri"/> is the search-results URL.</summary>
    Search,

    /// <summary>The input is refused (for example <c>javascript:</c>, which would run script in the current page).</summary>
    Blocked,
}

public sealed record AddressResolution(AddressKind Kind, Uri? Uri)
{
    public static AddressResolution Empty { get; } = new(AddressKind.Empty, null);
    public static AddressResolution Blocked { get; } = new(AddressKind.Blocked, null);
}

/// <summary>
/// Decides whether address-bar text is a URL or a search, the way mainstream browsers do:
/// <list type="bullet">
/// <item>An explicit scheme (<c>https://…</c>, <c>about:blank</c>, <c>file:///…</c>) is used as typed.</item>
/// <item><c>javascript:</c> and <c>vbscript:</c> are blocked.</item>
/// <item>Text containing spaces, or starting with <c>?</c>, is a search.</item>
/// <item>Something host-like is an address: a domain with a real-looking TLD (<c>example.com</c>, https),
/// <c>localhost</c>, an IP address, or a single word with a port (<c>intranet:8080</c>); those last ones use http.</item>
/// <item>Everything else (<c>hello</c>, <c>c#</c>, <c>3.14</c>) is a search.</item>
/// </list>
/// </summary>
public sealed partial class UrlOrSearchResolver
{
    public const string QueryPlaceholder = "{query}";

    private static readonly HashSet<string> BlockedSchemes =
        new(StringComparer.OrdinalIgnoreCase) { "javascript", "vbscript" };

    /// <summary>Schemes recognised even without "//" after the colon. Others (like "localhost:3000") are treated as host:port.</summary>
    private static readonly HashSet<string> SchemesWithoutSlashes =
        new(StringComparer.OrdinalIgnoreCase) { "about", "data", "mailto", "view-source" };

    private static readonly IdnMapping Idn = new();

    private volatile string _searchTemplate;

    public UrlOrSearchResolver(string searchTemplate)
    {
        _searchTemplate = Validate(searchTemplate, nameof(searchTemplate));
    }

    /// <summary>The search URL template in use. Changed when the user picks another search engine in Settings.</summary>
    public string SearchTemplate
    {
        get => _searchTemplate;
        set => _searchTemplate = Validate(value, nameof(value));
    }

    /// <summary>True for an http(s) URL containing <see cref="QueryPlaceholder"/>.</summary>
    public static bool IsValidSearchTemplate(string? template)
    {
        var text = template?.Trim();
        return !string.IsNullOrEmpty(text)
               && text.Contains(QueryPlaceholder, StringComparison.Ordinal)
               && Uri.TryCreate(text.Replace(QueryPlaceholder, "test", StringComparison.Ordinal), UriKind.Absolute, out var sample)
               && (sample.Scheme == Uri.UriSchemeHttps || sample.Scheme == Uri.UriSchemeHttp)
               && sample.Host.Length > 0;
    }

    private static string Validate(string template, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template, paramName);
        if (!IsValidSearchTemplate(template))
        {
            throw new ArgumentException($"The search template must be an http(s) URL containing {QueryPlaceholder}.", paramName);
        }

        return template.Trim();
    }

    public AddressResolution Resolve(string? input)
    {
        var text = input?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return AddressResolution.Empty;
        }

        // "?anything" forces a search, like Chrome and Edge.
        if (text[0] == '?')
        {
            var query = text[1..].Trim();
            return query.Length == 0 ? AddressResolution.Empty : Search(query);
        }

        var scheme = SchemeRegex().Match(text);
        if (scheme.Success)
        {
            var name = scheme.Groups["scheme"].Value;

            // Checked before anything else, so "javascript://%0aalert(1)" is caught too.
            if (BlockedSchemes.Contains(name))
            {
                return AddressResolution.Blocked;
            }

            var hasSlashes = text.AsSpan(scheme.Length).StartsWith("//", StringComparison.Ordinal);
            if (hasSlashes || SchemesWithoutSlashes.Contains(name))
            {
                return Uri.TryCreate(text, UriKind.Absolute, out var explicitUri)
                    ? new AddressResolution(AddressKind.Url, explicitUri)
                    : Search(text);
            }
        }

        if (text.Any(char.IsWhiteSpace))
        {
            return Search(text);
        }

        return TryResolveHost(text) is { } uri
            ? new AddressResolution(AddressKind.Url, uri)
            : Search(text);
    }

    private AddressResolution Search(string query) =>
        new(AddressKind.Search,
            new Uri(_searchTemplate.Replace(QueryPlaceholder, Uri.EscapeDataString(query), StringComparison.Ordinal)));

    /// <summary>Handles input without a scheme, such as "example.com/path" or "localhost:3000".</summary>
    private static Uri? TryResolveHost(string text)
    {
        var authorityEnd = text.IndexOfAny(['/', '?', '#']);
        var authority = authorityEnd < 0 ? text : text[..authorityEnd];
        var rest = authorityEnd < 0 ? string.Empty : text[authorityEnd..];

        // "@" means an e-mail address (search for it) or user-info (a phishing trick); neither should navigate.
        if (authority.Length == 0 || authority.Contains('@'))
        {
            return null;
        }

        if (!TrySplitHostAndPort(authority, out var host, out var port))
        {
            return null;
        }

        string scheme;
        if (IsLocalhost(host) || IsIPv4(host) || host.StartsWith('['))
        {
            scheme = "http"; // Local and IP servers rarely have a valid certificate.
        }
        else if (IsDomainName(host))
        {
            scheme = "https";
        }
        else if (IsHostLabel(host) && (port is not null || rest == "/"))
        {
            scheme = "http"; // "intranet:8080" or "intranet/"
        }
        else
        {
            return null;
        }

        return Uri.TryCreate($"{scheme}://{text}", UriKind.Absolute, out var uri) ? uri : null;
    }

    private static bool TrySplitHostAndPort(string authority, out string host, out string? port)
    {
        host = authority;
        port = null;

        if (authority.StartsWith('['))
        {
            // IPv6 literal: [::1] or [::1]:5000
            var close = authority.IndexOf(']');
            if (close < 0 || !IPAddress.TryParse(authority[1..close], out var address) || address.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return false;
            }

            host = authority[..(close + 1)];
            var after = authority[(close + 1)..];
            if (after.Length == 0)
            {
                return true;
            }

            if (after[0] != ':')
            {
                return false;
            }

            port = after[1..];
            return IsValidPort(port);
        }

        var colon = authority.IndexOf(':');
        if (colon < 0)
        {
            return true;
        }

        host = authority[..colon];
        port = authority[(colon + 1)..];
        return host.Length > 0 && IsValidPort(port);
    }

    private static bool IsValidPort(string port) =>
        port.Length is > 0 and <= 5
        && port.All(char.IsAsciiDigit)
        && int.Parse(port, CultureInfo.InvariantCulture) is > 0 and <= 65535;

    private static bool IsLocalhost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) && IsDomainLabels(host[..^".localhost".Length]));

    private static bool IsIPv4(string host)
    {
        if (!IPv4Regex().IsMatch(host))
        {
            return false;
        }

        return host.Split('.').All(part => int.Parse(part, CultureInfo.InvariantCulture) <= 255);
    }

    /// <summary>At least two valid labels and a TLD that looks real (letters only, or an IDN "xn--" TLD).</summary>
    private static bool IsDomainName(string host)
    {
        string ascii;
        try
        {
            ascii = Idn.GetAscii(host.TrimEnd('.')); // "münchen.de" -> "xn--mnchen-3ya.de"
        }
        catch (ArgumentException)
        {
            return false;
        }

        var labels = ascii.Split('.');
        if (labels.Length < 2 || !labels.All(IsHostLabel))
        {
            return false;
        }

        var tld = labels[^1];
        return tld.StartsWith("xn--", StringComparison.OrdinalIgnoreCase)
            || (tld.Length >= 2 && tld.All(char.IsAsciiLetter));
    }

    private static bool IsDomainLabels(string host) => host.Length > 0 && host.Split('.').All(IsHostLabel);

    /// <summary>One DNS label: 1-63 ASCII letters, digits or hyphens, not starting or ending with a hyphen.</summary>
    private static bool IsHostLabel(string label) =>
        label.Length is > 0 and <= 63
        && label[0] != '-'
        && label[^1] != '-'
        && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    [GeneratedRegex(@"^(?<scheme>[A-Za-z][A-Za-z0-9+.\-]*):", RegexOptions.CultureInvariant)]
    private static partial Regex SchemeRegex();

    [GeneratedRegex(@"^\d{1,3}(\.\d{1,3}){3}$", RegexOptions.CultureInvariant)]
    private static partial Regex IPv4Regex();
}
