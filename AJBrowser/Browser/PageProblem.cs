using Microsoft.Web.WebView2.Core;

namespace AJBrowser.Browser;

public enum PageProblemKind
{
    /// <summary>The page couldn't be loaded (no network, DNS failure, timeout…). Action: retry the URL.</summary>
    NavigationFailed,

    /// <summary>The page's renderer process crashed. Action: reload.</summary>
    RendererCrashed,

    /// <summary>The page's renderer stopped responding. Action: reload.</summary>
    RendererUnresponsive,

    /// <summary>The web engine couldn't start or keeps stopping. Action: restart the engine.</summary>
    EngineFailed,
}

/// <summary>Something that replaces the page with AJBrowser's own message panel.</summary>
public sealed record PageProblem(
    PageProblemKind Kind,
    string Title,
    string Message,
    string ActionText,
    string? Detail = null,
    Uri? Url = null)
{
    public static PageProblem RendererCrashed() =>
        new(PageProblemKind.RendererCrashed,
            "This page crashed",
            "Something went wrong while showing this page. Reloading usually fixes it.",
            "Reload");

    public static PageProblem RendererUnresponsive() =>
        new(PageProblemKind.RendererUnresponsive,
            "This page isn't responding",
            "The page may be busy or stuck. Reload it to start again.",
            "Reload");

    public static PageProblem EngineFailed(string? detail) =>
        new(PageProblemKind.EngineFailed,
            "The web engine stopped",
            "AJBrowser couldn't start or keep running the Microsoft Edge WebView2 engine.",
            "Try again",
            detail);
}

/// <summary>Decides which failed navigations get AJBrowser's error panel, and what it says.</summary>
public static class NavigationErrors
{
    /// <summary>
    /// Only network-level failures get our panel. Not shown when:
    /// <list type="bullet">
    /// <item>the server answered (HTTP status > 0): the site's own 404/500 page is more useful;</item>
    /// <item>certificate errors: WebView2's own warning page stays (plan §5.8);</item>
    /// <item><c>ConnectionAborted</c>: a navigation that turned into a download ends this way (spike F5);</item>
    /// <item><c>OperationCanceled</c>: the user pressed Stop or navigated elsewhere.</item>
    /// </list>
    /// </summary>
    public static bool ShouldShowErrorPanel(CoreWebView2WebErrorStatus status, int httpStatusCode) =>
        httpStatusCode == 0
        && status is CoreWebView2WebErrorStatus.HostNameNotResolved
            or CoreWebView2WebErrorStatus.CannotConnect
            or CoreWebView2WebErrorStatus.ServerUnreachable
            or CoreWebView2WebErrorStatus.Timeout
            or CoreWebView2WebErrorStatus.Disconnected
            or CoreWebView2WebErrorStatus.ConnectionReset
            or CoreWebView2WebErrorStatus.ErrorHttpInvalidServerResponse
            or CoreWebView2WebErrorStatus.RedirectFailed;

    public static PageProblem ToProblem(CoreWebView2WebErrorStatus status, Uri? url)
    {
        var (title, message) = status switch
        {
            CoreWebView2WebErrorStatus.HostNameNotResolved =>
                ("Can't find this site", "Check the address for typos. If it's correct, the site may be down or your connection may be having trouble."),
            CoreWebView2WebErrorStatus.Disconnected =>
                ("You're offline", "Check your network connection, then try again."),
            CoreWebView2WebErrorStatus.Timeout =>
                ("The site took too long to respond", "The site may be busy or your connection may be slow."),
            CoreWebView2WebErrorStatus.ConnectionReset =>
                ("The connection was reset", "The connection was interrupted. Try again in a moment."),
            CoreWebView2WebErrorStatus.ErrorHttpInvalidServerResponse =>
                ("The site sent an invalid response", "The site may be having problems. Try again later."),
            CoreWebView2WebErrorStatus.RedirectFailed =>
                ("This page isn't redirecting properly", "The site redirected too many times or to an invalid address."),
            _ =>
                ("Can't reach this site", "The site refused the connection or isn't responding."),
        };

        var host = url?.IsAbsoluteUri == true && !string.IsNullOrEmpty(url.Host) ? url.Host + " · " : string.Empty;
        return new PageProblem(PageProblemKind.NavigationFailed, title, message, "Try again", host + status, url);
    }
}
