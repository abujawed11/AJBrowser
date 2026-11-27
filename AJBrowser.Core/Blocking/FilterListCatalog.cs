namespace AJBrowser.Core.Blocking;

/// <summary>One filter list AJBrowser knows how to download.</summary>
/// <param name="Id">Stable id, used in settings and file names.</param>
/// <param name="Title">Name shown in Settings.</param>
/// <param name="Description">One line for Settings.</param>
/// <param name="Urls">Where to get it, tried in order (main address first, then mirrors). All https.</param>
/// <param name="Trusted">May use uBO's <c>trusted-*</c> scriptlets: only uBlock Origin's own lists, as in uBO.</param>
/// <param name="DefaultEnabled">On for new users.</param>
/// <param name="Homepage">Project page, for attribution.</param>
/// <param name="HostsFormat">The list is a hosts file (<c>127.0.0.1 ads.example</c>): each name blocks exactly that
/// site and its subdomains. (Bare names in ad-block syntax match anywhere in an address instead: <c>t.co</c> would block
/// every "…t.com" site.)</param>
public sealed record FilterListInfo(
    string Id,
    string Title,
    string Description,
    IReadOnlyList<string> Urls,
    bool Trusted,
    bool DefaultEnabled,
    string Homepage,
    bool HostsFormat = false)
{
    public byte ScriptletPermissions => Trusted ? FilterListText.TrustedScriptlets : (byte)0;
}

/// <summary>
/// The lists AJBrowser offers: uBlock Origin's default set, so blocking matches what uBO users get. Mirrors are the
/// ones uBO itself uses (its <c>assets.json</c>).
/// </summary>
public static class FilterListCatalog
{
    private const string Ubo = "https://ublockorigin.github.io/uAssets/filters/";
    private const string UboJsDelivr = "https://cdn.jsdelivr.net/gh/uBlockOrigin/uAssets@latest/filters/";
    private const string UboPages = "https://ublockorigin.pages.dev/filters/";
    private const string UboHome = "https://github.com/uBlockOrigin/uAssets";

    private static string[] UboUrls(string file) => [Ubo + file, UboJsDelivr + file, UboPages + file];

    public static IReadOnlyList<FilterListInfo> All { get; } =
    [
        new("ubo-filters", "uBlock filters – Ads", "uBlock Origin's own ad rules, including YouTube's.",
            UboUrls("filters.txt"), Trusted: true, DefaultEnabled: true, UboHome),
        new("ubo-quick-fixes", "uBlock filters – Quick fixes", "Fast fixes for sites that just changed (updated every few hours).",
            UboUrls("quick-fixes.txt"), Trusted: true, DefaultEnabled: true, UboHome),
        new("ubo-badware", "uBlock filters – Badware risks", "Scam, fake-download and malware sites.",
            UboUrls("badware.txt"), Trusted: true, DefaultEnabled: true, UboHome),
        new("ubo-privacy", "uBlock filters – Privacy", "Extra tracker rules.",
            UboUrls("privacy.txt"), Trusted: true, DefaultEnabled: true, UboHome),
        new("ubo-unbreak", "uBlock filters – Unbreak", "Exceptions that keep sites working.",
            UboUrls("unbreak.txt"), Trusted: true, DefaultEnabled: true, UboHome),
        new("easylist", "EasyList", "The main community list of ads.",
            ["https://easylist.to/easylist/easylist.txt", "https://ublockorigin.github.io/uAssets/thirdparties/easylist.txt"],
            Trusted: false, DefaultEnabled: true, "https://easylist.to/"),
        new("easyprivacy", "EasyPrivacy", "Trackers and analytics.",
            ["https://easylist.to/easylist/easyprivacy.txt", "https://ublockorigin.github.io/uAssets/thirdparties/easyprivacy.txt"],
            Trusted: false, DefaultEnabled: true, "https://easylist.to/"),
        new("peter-lowe", "Peter Lowe's Ad and tracking server list", "A long-standing list of ad and tracking servers.",
            // Hosts format, as uBO uses it. The "adblock" format is bare names ("t.co"), which adblock-rust reads as
            // patterns matching anywhere in an address (it broke superadblocktest.com, reddit.com…).
            ["https://pgl.yoyo.org/adservers/serverlist.php?hostformat=hosts&showintro=1&mimetype=plaintext",
             "https://cdn.jsdelivr.net/gh/uBlockOrigin/uAssets@latest/thirdparties/pgl.yoyo.org/as/serverlist"],
            Trusted: false, DefaultEnabled: true, "https://pgl.yoyo.org/adservers/", HostsFormat: true),
        new("easylist-cookie", "EasyList Cookie Notices", "Hides cookie banners (may break a few sites' consent pop-ups).",
            ["https://secure.fanboy.co.nz/fanboy-cookiemonster.txt", "https://easylist-downloads.adblockplus.org/fanboy-cookiemonster.txt"],
            Trusted: false, DefaultEnabled: false, "https://easylist.to/"),
    ];

    public static FilterListInfo? Find(string? id) =>
        All.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<string> DefaultIds { get; } = All.Where(l => l.DefaultEnabled).Select(l => l.Id).ToArray();

    /// <summary>Known ids only, in catalogue order, without duplicates (unknown ids from an old or newer settings
    /// file are dropped). Null means the defaults.</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? ids)
    {
        if (ids is null)
        {
            return DefaultIds;
        }

        var wanted = new HashSet<string>(ids.Where(i => i is not null), StringComparer.OrdinalIgnoreCase);
        return All.Where(l => wanted.Contains(l.Id)).Select(l => l.Id).ToArray();
    }
}
