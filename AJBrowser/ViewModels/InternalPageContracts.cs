using AJBrowser.Core.Logging;
using AJBrowser.Core.Navigation;
using AJBrowser.Services;

namespace AJBrowser.ViewModels;

/// <summary>Where a link from an internal page (or the bookmarks bar) should open.</summary>
public enum OpenTarget
{
    CurrentTab,
    NewForegroundTab,
    NewBackgroundTab,
}

/// <summary>What an internal page can ask of the tab showing it: open a web address (or another internal page).</summary>
public interface IPageHost
{
    void Open(Uri uri, OpenTarget target);
}

/// <summary>
/// The view model of an internal page (History, Bookmarks, Settings) shown in a tab instead of a web page.
/// MainWindow.xaml maps each implementation to its WPF view with a DataTemplate.
/// </summary>
public interface IInternalPage : IDisposable
{
    InternalPage Kind { get; }

    string Title { get; }

    /// <summary>The tab showing this page was selected (or reloaded): refresh data that may have changed meanwhile.</summary>
    void Activate();
}

/// <summary>Everything a tab needs besides its own <see cref="Browser.BrowserTab"/>. Shared by all tabs.</summary>
public sealed record TabServices(
    UrlOrSearchResolver Resolver,
    Func<Uri> HomePage,
    HistoryService History,
    BookmarkService Bookmarks,
    FaviconService Favicons,
    Func<InternalPage, IPageHost, IInternalPage> CreatePage,
    MediaDownloadService MediaDownloads,
    YtDlpService YtDlp,
    AppLogger Log);
