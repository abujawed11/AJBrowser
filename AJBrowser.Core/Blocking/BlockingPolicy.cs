using System.Collections.Frozen;
using AJBrowser.Core.Persistence;

namespace AJBrowser.Core.Blocking;

/// <summary>
/// Decides whether AJBrowser's own engine blocks on a page: the global switch, then the sites where the user turned
/// blocking off ("allowlist"). Turning a site off covers its subdomains too (off on <c>example.com</c> also means off on
/// <c>news.example.com</c>), and <c>www.</c> is ignored.
/// <para><see cref="IsEnabledFor"/> runs per navigation on the UI thread: it only reads an immutable set, no locks.
/// Changes are written to the database in the background.</para>
/// </summary>
public sealed class BlockingPolicy
{
    public const int MaxSiteKeyLength = 253;

    private readonly SiteSettingsRepository _repository;
    private readonly Func<DateTime> _utcNow;
    private FrozenSet<string> _disabledSites = FrozenSet<string>.Empty;
    private volatile bool _enabled = true;

    public BlockingPolicy(SiteSettingsRepository repository, Func<DateTime>? utcNow = null)
    {
        _repository = repository;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Raised after the global switch or a site's setting changed (on the caller's thread).</summary>
    public event EventHandler? Changed;

    /// <summary>The global switch (kept by the app with the rest of the ad-block state).</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled != value)
            {
                _enabled = value;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Sites with blocking turned off, sorted.</summary>
    public IReadOnlyList<string> DisabledSites => Volatile.Read(ref _disabledSites).Order(StringComparer.Ordinal).ToArray();

    public async Task LoadAsync()
    {
        var sites = await _repository.GetAdBlockDisabledSitesAsync().ConfigureAwait(false);
        Volatile.Write(ref _disabledSites, sites.ToFrozenSet(StringComparer.Ordinal));
    }

    /// <summary>Should the engine block requests on this page (the tab's top-level address)? Non-web pages: no.</summary>
    public bool IsEnabledFor(string? pageUrl) =>
        _enabled && SiteKey(pageUrl) is { } site && !IsSiteDisabledCore(site);

    /// <summary>Is blocking turned off for this site (by itself or a parent domain), ignoring the global switch?</summary>
    public bool IsSiteDisabled(string? pageUrlOrHost) => SiteKey(pageUrlOrHost) is { } site && IsSiteDisabledCore(site);

    /// <summary>Turns blocking on or off for the site of <paramref name="pageUrlOrHost"/>. Turning it back on also removes
    /// a parent-domain entry that covered it, so the switch always does what it says on this page.</summary>
    /// <returns>False when the address has no site (not http(s)).</returns>
    public async Task<bool> SetSiteEnabledAsync(string? pageUrlOrHost, bool enabled)
    {
        if (SiteKey(pageUrlOrHost) is not { } site)
        {
            return false;
        }

        var current = Volatile.Read(ref _disabledSites);
        var toRemove = enabled ? current.Where(s => Covers(s, site)).ToArray() : [];
        var updated = enabled ? current.Except(toRemove) : current.Append(site);
        Volatile.Write(ref _disabledSites, updated.ToFrozenSet(StringComparer.Ordinal));
        Changed?.Invoke(this, EventArgs.Empty);

        if (enabled)
        {
            foreach (var entry in toRemove)
            {
                await _repository.SetAdBlockDisabledAsync(entry, false, _utcNow()).ConfigureAwait(false);
            }
        }
        else
        {
            await _repository.SetAdBlockDisabledAsync(site, true, _utcNow()).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// The key a page's site is stored under: lower-case host without a leading <c>www.</c>, from an http(s) address
    /// or a bare host name. Null for anything else (internal pages, file:, data:…).
    /// </summary>
    public static string? SiteKey(string? pageUrlOrHost)
    {
        if (string.IsNullOrWhiteSpace(pageUrlOrHost))
        {
            return null;
        }

        var text = pageUrlOrHost.Trim();
        string host;
        if (text.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return null;
            }

            host = uri.IdnHost;
        }
        else
        {
            if (Uri.CheckHostName(text) is UriHostNameType.Unknown or UriHostNameType.Basic)
            {
                return null;
            }

            host = Uri.TryCreate("https://" + text, UriKind.Absolute, out var uri) ? uri.IdnHost : text;
        }

        host = host.TrimEnd('.').ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal) && host.Length > 4)
        {
            host = host[4..];
        }

        return host.Length is > 0 and <= MaxSiteKeyLength ? host : null;
    }

    private bool IsSiteDisabledCore(string site)
    {
        var disabled = Volatile.Read(ref _disabledSites);
        if (disabled.Count == 0)
        {
            return false;
        }

        // The site itself, then each parent domain: a.b.example.com → b.example.com → example.com → com.
        var candidate = site.AsSpan();
        var lookup = disabled.GetAlternateLookup<ReadOnlySpan<char>>();
        while (true)
        {
            if (lookup.Contains(candidate))
            {
                return true;
            }

            var dot = candidate.IndexOf('.');
            if (dot < 0)
            {
                return false;
            }

            candidate = candidate[(dot + 1)..];
        }
    }

    /// <summary>Does a disabled entry apply to <paramref name="site"/> (same site or a parent domain)?</summary>
    private static bool Covers(string entry, string site) =>
        site == entry || site.EndsWith("." + entry, StringComparison.Ordinal);
}
