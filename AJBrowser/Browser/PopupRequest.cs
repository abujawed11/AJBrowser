using Microsoft.Web.WebView2.Core;

namespace AJBrowser.Browser;

/// <summary>
/// A page asked to open a new window (target=_blank link, window.open, "Sign in with…" popup) and the user
/// caused it. Whoever handles <see cref="BrowserTab.PopupRequested"/> must call exactly one of
/// <see cref="OpenInAsync"/> or <see cref="Cancel"/>; WebView2 waits (via a deferral) until then.
/// </summary>
public sealed class PopupRequest
{
    private readonly CoreWebView2NewWindowRequestedEventArgs _args;
    private readonly CoreWebView2Deferral _deferral;
    private bool _completed;

    internal PopupRequest(CoreWebView2NewWindowRequestedEventArgs args, CoreWebView2Deferral deferral, Uri? uri)
    {
        _args = args;
        _deferral = deferral;
        Uri = uri;
    }

    public Uri? Uri { get; }

    /// <summary>
    /// Starts <paramref name="tab"/>'s WebView (running our full Configure() setup, spike F10) and hands it to
    /// WebView2 as the new window. WebView2 then loads the page into it and keeps window.opener working.
    /// The tab must not have navigated anywhere yet.
    /// </summary>
    public async Task OpenInAsync(BrowserTab tab)
    {
        try
        {
            await tab.InitializeAsync();
            if (tab.Core is { } core)
            {
                _args.NewWindow = core;
            }

            _args.Handled = true; // without NewWindow (engine failed), this cancels the popup
        }
        finally
        {
            Complete();
        }
    }

    public void Cancel()
    {
        _args.Handled = true;
        Complete();
    }

    private void Complete()
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        _deferral.Complete();
    }
}
