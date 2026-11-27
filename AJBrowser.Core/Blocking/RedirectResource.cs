namespace AJBrowser.Core.Blocking;

/// <summary>
/// A <c>$redirect=</c> answer (for example uBO's empty <c>noop.js</c> or a 1×1 GIF) turned into a response body.
/// The engine hands these over as <c>data:</c> URLs; the browser answers the request with the decoded bytes so the page
/// gets a harmless stand-in instead of a failed request (which some sites detect).
/// </summary>
public sealed record RedirectResource(string ContentType, byte[] Body)
{
    /// <summary>Redirect resources are small scripts and images; anything bigger is refused.</summary>
    public const int MaxBodyBytes = 512 * 1024;

    /// <summary>Decodes <c>data:[type][;base64],data</c>. Null for anything else or anything malformed.</summary>
    public static RedirectResource? TryDecode(string? dataUrl)
    {
        if (string.IsNullOrEmpty(dataUrl) || !dataUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var comma = dataUrl.IndexOf(',', 5);
        if (comma < 0)
        {
            return null;
        }

        var meta = dataUrl[5..comma];
        var payload = dataUrl[(comma + 1)..];
        var isBase64 = meta.EndsWith(";base64", StringComparison.OrdinalIgnoreCase);
        var type = (isBase64 ? meta[..^7] : meta).Trim();
        if (type.Length == 0)
        {
            type = "text/plain";
        }

        if (type.AsSpan().IndexOfAny("\r\n") >= 0)
        {
            return null;
        }

        try
        {
            var body = isBase64
                ? Convert.FromBase64String(payload)
                : System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
            return body.Length > MaxBodyBytes ? null : new RedirectResource(type, body);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
