using System.Collections.ObjectModel;
using System.Windows.Media;
using AJBrowser.Browser;
using AJBrowser.Core.Navigation;
using AJBrowser.Core.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AJBrowser.ViewModels;

/// <summary>A link or popup asked for a new tab. <see cref="Select"/> is false for "Open link in new tab" (background).</summary>
public sealed record NewTabRequest(Uri Uri, bool Select);

/// <summary>
/// UI state for one tab: address bar text, title, favicon, loading, back/forward, zoom, find, the problem panel,
/// the bookmark star and address-bar suggestions. Think of it as the React state + handlers for a tab;
/// <see cref="BrowserTab"/> is the "API" it calls.
/// <para>
/// A tab shows either a web page (the WebView2 in <see cref="BrowserView"/>) or an internal page
/// (<see cref="InternalPageContent"/>: History, Bookmarks, Settings). The web engine starts only when the tab
/// first needs it, so restored background tabs and internal pages cost almost nothing.
/// </para>
/// </summary>
public sealed partial class TabViewModel : ObservableObject, IDisposable, IPageHost
{
    private const string AppName = "AJBrowser";
    private const string NewTabTitle = "New tab";

    private readonly BrowserTab _browser;
    private readonly TabServices _services;

    private string? _lastUrl;
    private bool _isEditingAddress;
    private bool _isAddressDirty;   // the user typed something that hasn't been navigated to or reverted
    private bool _isSettingAddress; // we are changing AddressText ourselves, not the user
    private bool _started;          // the web engine has been started (or adopted by a popup)
    private Uri? _deferredUri;      // restored background tab: load this when the tab is first shown
    private string? _recordedUrl;   // last URL written to history, and the title written with it
    private string? _recordedTitle;

    public TabViewModel(BrowserTab browser, TabServices services)
    {
        _browser = browser;
        _services = services;
        Suggestions = new SuggestionsViewModel(services.History, services.Bookmarks, services.Favicons, services.Log);

        _browser.StateChanged += OnBrowserStateChanged;
        _browser.NavigationStarted += OnBrowserNavigationStarted;
        _browser.PageLoaded += OnBrowserPageLoaded;
        _browser.FaviconLoaded += OnBrowserFaviconLoaded;
        _browser.ProblemOccurred += OnBrowserProblemOccurred;
        _browser.PopupRequested += OnBrowserPopupRequested;
        _browser.PopupBlocked += OnBrowserPopupBlocked;
        _browser.OpenInNewTabRequested += OnBrowserOpenInNewTabRequested;
        _browser.CloseRequested += OnBrowserCloseRequested;
        _services.Bookmarks.Changed += OnBookmarksChanged;
    }

    /// <summary>A link wants a new tab (handled by <see cref="MainWindowViewModel"/>).</summary>
    public event EventHandler<NewTabRequest>? NewTabRequested;

    /// <summary>The user caused a popup: the handler must create a tab and call <see cref="AdoptPopupAsync"/> or cancel.</summary>
    public event EventHandler<PopupRequest>? PopupRequested;

    /// <summary>The page asked to close its own tab (window.close()).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The page element to show in the window (hosts the WebView2 control).</summary>
    public object BrowserView => _browser.View;

    /// <summary>What this tab shows, for the session and the closed-tabs list: the web URL, an ajbrowser:// address, or the not-yet-loaded restored URL.</summary>
    public string? Url =>
        InternalPageContent is { } page ? InternalPages.ToUrl(page.Kind)
        : _browser.Url ?? _deferredUri?.AbsoluteUri;

    public SuggestionsViewModel Suggestions { get; }

    // ---------------------------------------------------------------- bindable state

    /// <summary>Text in the address bar: the clean URL normally, the full URL while editing, or what the user typed.</summary>
    [ObservableProperty]
    public partial string AddressText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    public partial string Title { get; set; } = NewTabTitle;

    [ObservableProperty]
    public partial ImageSource? Favicon { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoBackCommand))]
    public partial bool CanGoBack { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoForwardCommand))]
    public partial bool CanGoForward { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>Set by <see cref="MainWindowViewModel"/>: this is the tab being shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPageShown), nameof(IsInternalPageShown))]
    public partial bool IsSelected { get; set; }

    /// <summary>When set, the page is hidden and a message panel is shown instead.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem), nameof(IsPageShown))]
    public partial PageProblem? Problem { get; set; }

    public bool HasProblem => Problem is not null;

    /// <summary>The internal page shown instead of the web page, or null for a web page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInternal), nameof(IsPageShown), nameof(IsInternalPageShown), nameof(CanBookmark))]
    public partial IInternalPage? InternalPageContent { get; set; }

    public bool IsInternal => InternalPageContent is not null;

    /// <summary>
    /// Only the selected tab's WebView is visible, and it's hidden while a problem panel or internal page is shown:
    /// WPF can't draw on top of a WebView2 ("airspace", plan §3), so they take turns.
    /// </summary>
    public bool IsPageShown => IsSelected && Problem is null && !IsInternal;

    public bool IsInternalPageShown => IsSelected && IsInternal;

    [ObservableProperty]
    public partial bool IsPageFullScreen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomText), nameof(IsZoomed))]
    public partial double ZoomFactor { get; set; } = ZoomLevels.Default;

    public string ZoomText => ZoomLevels.ToPercentText(ZoomFactor);

    public bool IsZoomed => !ZoomLevels.IsDefault(ZoomFactor);

    /// <summary>Popups blocked on the current page (the URL is null when the page didn't give one).</summary>
    public ObservableCollection<Uri?> BlockedPopups { get; } = [];

    [ObservableProperty]
    public partial bool HasBlockedPopups { get; set; }

    [ObservableProperty]
    public partial bool IsFindBarOpen { get; set; }

    [ObservableProperty]
    public partial string FindText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FindStatusText { get; set; } = string.Empty;

    /// <summary>The current web page is bookmarked (filled star).</summary>
    [ObservableProperty]
    public partial bool IsBookmarked { get; set; }

    /// <summary>There is a web page to bookmark.</summary>
    public bool CanBookmark => !IsInternal && !string.IsNullOrEmpty(_browser.Url);

    /// <summary>The star's panel: set while it's open.</summary>
    [ObservableProperty]
    public partial BookmarkEditorViewModel? BookmarkEditor { get; set; }

    [ObservableProperty]
    public partial bool IsBookmarkEditorOpen { get; set; }

    public string WindowTitle => string.IsNullOrWhiteSpace(Title) ? AppName : $"{Title} - {AppName}";

    // ---------------------------------------------------------------- lifecycle

    /// <summary>Loads <paramref name="uri"/> (or the home page): a web page starts the engine; an ajbrowser:// address shows that page.</summary>
    public async Task StartAsync(Uri? uri = null)
    {
        var target = uri ?? _services.HomePage();
        if (InternalPages.TryParse(target.AbsoluteUri) is { } page)
        {
            ShowInternalPage(page);
            return;
        }

        await StartEngineAsync(target);
    }

    /// <summary>A restored background tab: shows its title now, loads the page when the tab is first selected.</summary>
    public void StartDeferred(Uri uri, string title)
    {
        if (InternalPages.TryParse(uri.AbsoluteUri) is { } page)
        {
            ShowInternalPage(page); // internal pages are cheap: show right away
            return;
        }

        _deferredUri = uri;
        Title = string.IsNullOrWhiteSpace(title) ? UrlDisplay.ForAddressBar(uri.AbsoluteUri) : title;
        SetAddress(UrlDisplay.ForAddressBar(uri.AbsoluteUri));
    }

    /// <summary>Starts this (new, never navigated) tab as the window a page opened, so window.opener keeps working.</summary>
    public async Task AdoptPopupAsync(PopupRequest request)
    {
        if (_started)
        {
            request.Cancel();
            return;
        }

        _started = true;
        await request.OpenInAsync(_browser);
    }

    public void FocusPage()
    {
        if (!IsInternal)
        {
            _browser.FocusPage();
        }
    }

    private async Task StartEngineAsync(Uri uri)
    {
        _deferredUri = null;
        _started = true;
        _browser.Navigate(uri); // queued until the engine is ready
        await _browser.InitializeAsync();
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (!value)
        {
            return;
        }

        if (_deferredUri is { } deferred)
        {
            _ = StartEngineAsync(deferred); // never throws: problems show in the page area
        }

        InternalPageContent?.Activate();
    }

    // ---------------------------------------------------------------- navigation (also IPageHost for internal pages)

    /// <summary>Opens a web address or an ajbrowser:// page here or in a new tab.</summary>
    public void Open(Uri uri, OpenTarget target)
    {
        if (target != OpenTarget.CurrentTab)
        {
            NewTabRequested?.Invoke(this, new NewTabRequest(uri, Select: target == OpenTarget.NewForegroundTab));
            return;
        }

        if (InternalPages.TryParse(uri.AbsoluteUri) is { } page)
        {
            ShowInternalPage(page);
        }
        else
        {
            Navigate(uri);
        }
    }

    /// <summary>Shows an internal page in this tab (the web page, if any, stays loaded behind it).</summary>
    public void ShowInternalPage(InternalPage page)
    {
        Suggestions.Close();
        _isAddressDirty = false;
        if (InternalPageContent?.Kind == page)
        {
            InternalPageContent.Activate();
        }
        else
        {
            InternalPageContent?.Dispose();
            InternalPageContent = _services.CreatePage(page, this);
        }

        Problem = null;
        IsFindBarOpen = false;
        Title = InternalPageContent.Title;
        Favicon = null;
        IsLoading = false;
        CanGoBack = _started && !string.IsNullOrEmpty(_browser.Url);
        CanGoForward = false;
        IsBookmarked = false;
        SetAddress(InternalPages.ToUrl(page));
    }

    /// <summary>Back from an internal page to the web page underneath.</summary>
    private void LeaveInternalPage()
    {
        if (InternalPageContent is null)
        {
            return;
        }

        InternalPageContent.Dispose();
        InternalPageContent = null;
        _lastUrl = null; // force the address bar to refresh from the browser
        OnBrowserStateChanged(this, EventArgs.Empty);
    }

    private void Navigate(Uri uri)
    {
        Problem = null;
        if (IsInternal)
        {
            InternalPageContent!.Dispose();
            InternalPageContent = null;
            _lastUrl = null;
        }

        if (!_started)
        {
            _ = StartEngineAsync(uri); // never throws
        }
        else
        {
            _browser.Navigate(uri);
        }
    }

    // ---------------------------------------------------------------- address bar (called by the AddressBar control)

    public void BeginAddressEdit()
    {
        _isEditingAddress = true;
        if (!_isAddressDirty && !IsInternal)
        {
            SetAddress(_browser.Url ?? _deferredUri?.AbsoluteUri ?? string.Empty); // show the full URL while editing
        }
    }

    public void EndAddressEdit()
    {
        _isEditingAddress = false;
        Suggestions.Close();
        if (!_isAddressDirty)
        {
            SetAddress(CurrentDisplayAddress());
        }
    }

    partial void OnAddressTextChanged(string value)
    {
        if (!_isSettingAddress && _isEditingAddress)
        {
            _isAddressDirty = true;
            Suggestions.Update(value);
        }
    }

    /// <summary>Enter in the address bar: the highlighted suggestion, an ajbrowser:// page, a URL or a search.</summary>
    [RelayCommand]
    private void NavigateToAddress()
    {
        if (Suggestions.Selected is { } suggestion)
        {
            NavigateToSuggestion(suggestion);
            return;
        }

        Suggestions.Close();
        if (InternalPages.TryParse(AddressText) is { } page)
        {
            ShowInternalPage(page);
            return;
        }

        var resolution = _services.Resolver.Resolve(AddressText);
        switch (resolution.Kind)
        {
            case AddressKind.Url or AddressKind.Search:
                // Keep the target visible until the page reports its new URL (then OnBrowserStateChanged takes over).
                SetAddress(resolution.Uri!.AbsoluteUri);
                _isAddressDirty = true;
                Navigate(resolution.Uri);
                _browser.FocusPage();
                break;

            case AddressKind.Blocked:
                _services.Log.Info($"Blocked address bar input: {Redactor.Text(AddressText)}");
                break;

            case AddressKind.Empty:
                RevertAddress();
                break;
        }
    }

    /// <summary>A suggestion was clicked, or highlighted and Enter pressed.</summary>
    public void NavigateToSuggestion(SuggestionItem suggestion)
    {
        Suggestions.Close();
        if (!Uri.TryCreate(suggestion.Url, UriKind.Absolute, out var uri))
        {
            return;
        }

        SetAddress(uri.AbsoluteUri);
        _isAddressDirty = true;
        Navigate(uri);
        _browser.FocusPage();
    }

    /// <summary>Esc in the address bar: throw away the edit.</summary>
    [RelayCommand]
    private void RevertAddress()
    {
        Suggestions.Close();
        _isAddressDirty = false;
        SetAddress(_isEditingAddress && !IsInternal ? _browser.Url ?? _deferredUri?.AbsoluteUri ?? string.Empty : CurrentDisplayAddress());
    }

    private string CurrentDisplayAddress() =>
        InternalPageContent is { } page ? InternalPages.ToUrl(page.Kind)
        : UrlDisplay.ForAddressBar(_browser.Url ?? _deferredUri?.AbsoluteUri);

    // ---------------------------------------------------------------- toolbar commands

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack()
    {
        if (IsInternal)
        {
            LeaveInternalPage(); // back to the web page behind the internal page
        }
        else
        {
            _browser.GoBack();
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void GoForward() => _browser.GoForward();

    [RelayCommand]
    private async Task ReloadOrStop()
    {
        if (IsLoading)
        {
            Stop();
        }
        else
        {
            await Reload();
        }
    }

    [RelayCommand]
    private async Task Reload()
    {
        if (InternalPageContent is { } page)
        {
            page.Activate();
        }
        else if (HasProblem)
        {
            await Retry();
        }
        else
        {
            _browser.Reload();
        }
    }

    public void Stop() => _browser.Stop();

    [RelayCommand]
    private void Home() => Open(_services.HomePage(), OpenTarget.CurrentTab);

    [RelayCommand]
    private void Print()
    {
        if (!IsInternal)
        {
            _browser.Print();
        }
    }

    [RelayCommand]
    private void ZoomIn() => _browser.SetZoom(ZoomLevels.ZoomIn(ZoomFactor));

    [RelayCommand]
    private void ZoomOut() => _browser.SetZoom(ZoomLevels.ZoomOut(ZoomFactor));

    [RelayCommand]
    private void ResetZoom() => _browser.SetZoom(ZoomLevels.Default);

    /// <summary>The button on the problem panel.</summary>
    [RelayCommand]
    private async Task Retry()
    {
        var problem = Problem;
        Problem = null; // show the WebView again first; it can't start while hidden

        switch (problem?.Kind)
        {
            case PageProblemKind.NavigationFailed when problem.Url is not null:
                _browser.Navigate(problem.Url);
                break;

            case PageProblemKind.EngineFailed:
                await _browser.RestartEngineAsync();
                break;

            default:
                _browser.Reload();
                break;
        }
    }

    /// <summary>Opens a blocked popup as a normal tab (from the blocked-popups menu).</summary>
    [RelayCommand]
    private void OpenBlockedPopup(Uri? uri)
    {
        if (uri is null)
        {
            return;
        }

        BlockedPopups.Remove(uri);
        HasBlockedPopups = BlockedPopups.Count > 0;
        NewTabRequested?.Invoke(this, new NewTabRequest(uri, Select: true));
    }

    // ---------------------------------------------------------------- bookmarks (star button, Ctrl+D)

    /// <summary>The star: bookmarks the page if needed, then opens the edit panel.</summary>
    [RelayCommand]
    private async Task BookmarkPage()
    {
        if (!CanBookmark || _browser.Url is not { } url)
        {
            return;
        }

        var existing = _services.Bookmarks.FindByUrl(url);
        var isNew = existing is null;
        existing ??= await _services.Bookmarks.AddAsync(parentId: null, Title, url);
        if (existing is null)
        {
            return; // saving failed (logged)
        }

        BookmarkEditor = new BookmarkEditorViewModel(_services.Bookmarks, existing, isNew);
        IsBookmarkEditorOpen = true;
    }

    /// <summary>The edit panel closed (Done, Remove, Esc or a click elsewhere): save what changed.</summary>
    public async Task CloseBookmarkEditorAsync()
    {
        IsBookmarkEditorOpen = false;
        if (BookmarkEditor is { } editor)
        {
            BookmarkEditor = null;
            await editor.CommitAsync();
        }
    }

    private void RefreshBookmarkState()
    {
        IsBookmarked = !IsInternal && _services.Bookmarks.FindByUrl(_browser.Url) is not null;
        OnPropertyChanged(nameof(CanBookmark));
    }

    private void OnBookmarksChanged(object? sender, EventArgs e) => RefreshBookmarkState();

    // ---------------------------------------------------------------- find in page

    public void OpenFind()
    {
        if (!IsInternal)
        {
            IsFindBarOpen = true;
        }
    }

    [RelayCommand]
    private void CloseFind()
    {
        IsFindBarOpen = false;
        _browser.StopFind();
        _browser.FocusPage();
    }

    [RelayCommand]
    private void FindNext()
    {
        if (FindText.Length > 0)
        {
            _browser.FindNext();
        }
    }

    [RelayCommand]
    private void FindPrevious()
    {
        if (FindText.Length > 0)
        {
            _browser.FindPrevious();
        }
    }

    partial void OnFindTextChanged(string value)
    {
        // Each change restarts the search, like other browsers' find bars.
        _ = _browser.FindAsync(value);
        UpdateFindStatus();
    }

    private void UpdateFindStatus() =>
        FindStatusText = FindStatus.Format(FindText, _browser.FindActiveMatch, _browser.FindMatchCount);

    // ---------------------------------------------------------------- browser events

    private void OnBrowserStateChanged(object? sender, EventArgs e)
    {
        // The engine's page title arrived after the visit was recorded: update the history entry.
        if (_recordedUrl is not null && _browser.Url == _recordedUrl
            && !string.IsNullOrWhiteSpace(_browser.Title) && _browser.Title != _recordedTitle)
        {
            _recordedTitle = _browser.Title;
            _services.History.UpdateTitle(_recordedUrl, _browser.Title);
        }

        if (IsInternal)
        {
            // The web page behind an internal page may still be loading; only Back's target changes.
            CanGoBack = !string.IsNullOrEmpty(_browser.Url);
            return;
        }

        Title = !string.IsNullOrWhiteSpace(_browser.Title)
            ? _browser.Title
            : _browser.Url is { Length: > 0 } url ? UrlDisplay.ForAddressBar(url) : NewTabTitle;
        Favicon = _browser.Favicon;
        CanGoBack = _browser.CanGoBack;
        CanGoForward = _browser.CanGoForward;
        IsLoading = _browser.IsLoading;
        IsPageFullScreen = _browser.IsPageFullScreen;
        ZoomFactor = _browser.ZoomFactor;
        if (IsFindBarOpen)
        {
            UpdateFindStatus();
        }

        if (_browser.Url != _lastUrl)
        {
            _lastUrl = _browser.Url;
            RefreshBookmarkState();

            // Never change the text while the box has focus: the user may be about to type (or typing) into it,
            // and replacing the text would drop the selection (typing then lands in front of the URL).
            // EndAddressEdit shows the new address when the box loses focus.
            if (!_isEditingAddress)
            {
                _isAddressDirty = false;
                SetAddress(UrlDisplay.ForAddressBar(_browser.Url));
            }
        }
    }

    private void OnBrowserPageLoaded(object? sender, string url)
    {
        _recordedUrl = url;
        _recordedTitle = _browser.Title;
        _services.History.RecordVisit(url, _browser.Title);
    }

    /// <summary>Remember the site's icon so History, Bookmarks and suggestions can show it.</summary>
    private void OnBrowserFaviconLoaded(object? sender, (string? PageUrl, byte[] Png) icon) =>
        _services.Favicons.Remember(icon.PageUrl, icon.Png);

    private void OnBrowserNavigationStarted(object? sender, EventArgs e)
    {
        // A new page is loading, so an old error/crash message and old blocked popups no longer apply.
        Problem = null;
        if (BlockedPopups.Count > 0)
        {
            BlockedPopups.Clear();
            HasBlockedPopups = false;
        }
    }

    private void OnBrowserProblemOccurred(object? sender, PageProblem problem)
    {
        if (!IsInternal)
        {
            Problem = problem;
        }
    }

    private void OnBrowserPopupRequested(object? sender, PopupRequest request)
    {
        if (PopupRequested is null)
        {
            request.Cancel();
            return;
        }

        PopupRequested.Invoke(this, request);
    }

    private void OnBrowserPopupBlocked(object? sender, Uri? uri)
    {
        if (!BlockedPopups.Contains(uri))
        {
            BlockedPopups.Add(uri);
        }

        HasBlockedPopups = true;
    }

    private void OnBrowserOpenInNewTabRequested(object? sender, Uri uri) =>
        NewTabRequested?.Invoke(this, new NewTabRequest(uri, Select: false));

    private void OnBrowserCloseRequested(object? sender, EventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void SetAddress(string text)
    {
        _isSettingAddress = true;
        try
        {
            AddressText = text;
        }
        finally
        {
            _isSettingAddress = false;
        }
    }

    public void Dispose()
    {
        _browser.StateChanged -= OnBrowserStateChanged;
        _browser.NavigationStarted -= OnBrowserNavigationStarted;
        _browser.PageLoaded -= OnBrowserPageLoaded;
        _browser.FaviconLoaded -= OnBrowserFaviconLoaded;
        _browser.ProblemOccurred -= OnBrowserProblemOccurred;
        _browser.PopupRequested -= OnBrowserPopupRequested;
        _browser.PopupBlocked -= OnBrowserPopupBlocked;
        _browser.OpenInNewTabRequested -= OnBrowserOpenInNewTabRequested;
        _browser.CloseRequested -= OnBrowserCloseRequested;
        _services.Bookmarks.Changed -= OnBookmarksChanged;
        Suggestions.Dispose();
        InternalPageContent?.Dispose();
        _browser.Dispose();
    }
}
