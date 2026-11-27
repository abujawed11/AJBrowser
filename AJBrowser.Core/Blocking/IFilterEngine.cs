namespace AJBrowser.Core.Blocking;

/// <summary>
/// A compiled set of filter lists that answers "block this request?" and "what to hide on this page?".
/// Immutable once built: new lists mean a new engine, swapped in atomically by the caller.
/// <para>Every method is safe to call from any thread at the same time, never throws for bad input, and answers
/// "allow / nothing to do" when it can't decide, so a filter problem never breaks browsing.</para>
/// </summary>
public interface IFilterEngine : IDisposable
{
    /// <summary>Checks one request. <paramref name="sourceUrl"/> is the page (or frame) that made it; it decides
    /// first- vs third-party and <c>$domain=</c> matches.</summary>
    FilterDecision Check(string url, string sourceUrl, RequestKind kind);

    /// <summary>Selectors, scriptlets and flags for a page about to load.</summary>
    CosmeticResources GetCosmeticResources(string pageUrl);

    /// <summary>Generic hide selectors (<c>##.ad-banner</c>) for class names and ids found on a page.</summary>
    IReadOnlyList<string> GetHiddenClassIdSelectors(
        IEnumerable<string> classes,
        IEnumerable<string> ids,
        IEnumerable<string> exceptions);

    /// <summary>The compiled engine as bytes, to load quickly at the next start (resources aren't included).</summary>
    byte[] Serialize();
}
