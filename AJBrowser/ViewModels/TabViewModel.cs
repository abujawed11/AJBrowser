using System.Collections.ObjectModel;
using System.Windows.Media;
using AJBrowser.Browser;
using AJBrowser.Core.Logging;
using AJBrowser.Core.Navigation;
using AJBrowser.Core.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AJBrowser.ViewModels;

/// <summary>A link or popup asked for a new tab. <see cref="Select"/> is false for "Open link in new tab" (background).</summary>
public sealed record NewTabRequest(Uri Uri, bool Select);

/// <summary>
/// UI state for one tab: address bar text, title, favicon, loading, back/forward, zoom, find, and the problem panel.
/// Think of it as the React state + handlers for a tab; <see cref="BrowserTab"/> is the "API" it calls.
/// </summary>
public sealed partial class TabViewModel : ObservableObject, IDisposable
{
    private const string AppName = "AJBrowser";
    private const string NewTabTitle = "New tab";

    private readonly BrowserTab _browser;
    private readonly UrlOrSearchResolver _resolver;
    private readonly Uri _homePage;
    private readonly AppLogger _log;

    private string? _lastUrl;
    private bool _isEditingAddress;
    private bool _isAddressDirty;   // the user typed something that hasn't been navigated to or reverted
    private bool _isSettingAddress; // we are changing AddressText ourselves, not the user
    private bool _started;

    public TabViewModel(BrowserTab browser, UrlOrSearchResolver resolver, Uri homePage, AppLogger log)
    {
        _browser = browser;
        _resolver = resolver;
        _homePage = homePage;
        _log = log;

        _browser.StateChanged += OnBrowserStateChanged;
        _browser.NavigationStarted += OnBrowserNavigationStarted;
        _browser.ProblemOccurred += OnBrowserProblemOccurred;
        _browser.PopupRequested += OnBrowserPopupRequested;
        _browser.PopupBlocked += OnBrowserPopupBlocked;
        _browser.OpenInNewTabRequested += OnBrowserOpenInNewTabRequested;
        _browser.CloseRequested += OnBrowserCloseRequested;
    }

    /// <summary>A link wants a new tab (handled by <see cref="MainWindowViewModel"/>).</summary>
    public event EventHandler<NewTabRequest>? NewTabRequested;

    /// <summary>The user caused a popup: the handler must create a tab and call <see cref="AdoptPopupAsync"/> or cancel.</summary>
    public event EventHandler<PopupRequest>? PopupRequested;

    /// <summary>The page asked to close its own tab (window.close()).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The page element to show in the window (hosts the WebView2 control).</summary>
    public object BrowserView => _browser.View;

    /// <summary>Current URL, for the closed-tabs list.</summary>
    public string? Url => _browser.Url;

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
    [NotifyPropertyChangedFor(nameof(IsPageShown))]
    public partial bool IsSelected { get; set; }

    /// <summary>When set, the page is hidden and a message panel is shown instead.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem), nameof(IsPageShown))]
    public partial PageProblem? Problem { get; set; }

    public bool HasProblem => Problem is not null;

    /// <summary>
    /// Only the selected tab's WebView is visible, and it's hidden while a problem panel is shown: WPF can't
    /// draw on top of a WebView2 ("airspace", plan §3), so the panel and the page take turns.
    /// </summary>
    public bool IsPageShown => IsSelected && Problem is null;

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

    public string WindowTitle => string.IsNullOrWhiteSpace(Title) ? AppName : $"{Title} - {AppName}";

    // ---------------------------------------------------------------- lifecycle

    /// <summary>Starts the web engine and loads <paramref name="uri"/> (or the home page).</summary>
    public async Task StartAsync(Uri? uri = null)
    {
        _started = true;
        _browser.Navigate(uri ?? _homePage); // queued until the engine is ready
        await _browser.InitializeAsync();
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

    public void FocusPage() => _browser.FocusPage();

    // ---------------------------------------------------------------- address bar (called by the AddressBar control)

    public void BeginAddressEdit()
    {
        _isEditingAddress = true;
        if (!_isAddressDirty)
        {
            SetAddress(_browser.Url ?? string.Empty); // show the full URL while editing
        }
    }

    public void EndAddressEdit()
    {
        _isEditingAddress = false;
        if (!_isAddressDirty)
        {
            SetAddress(UrlDisplay.ForAddressBar(_browser.Url));
        }
    }

    partial void OnAddressTextChanged(string value)
    {
        if (!_isSettingAddress && _isEditingAddress)
        {
            _isAddressDirty = true;
        }
    }

    /// <summary>Enter in the address bar.</summary>
    [RelayCommand]
    private void NavigateToAddress()
    {
        var resolution = _resolver.Resolve(AddressText);
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
                _log.Info($"Blocked address bar input: {Redactor.Text(AddressText)}");
                break;

            case AddressKind.Empty:
                RevertAddress();
                break;
        }
    }

    /// <summary>Esc in the address bar: throw away the edit.</summary>
    [RelayCommand]
    private void RevertAddress()
    {
        _isAddressDirty = false;
        SetAddress(_isEditingAddress ? _browser.Url ?? string.Empty : UrlDisplay.ForAddressBar(_browser.Url));
    }

    // ---------------------------------------------------------------- toolbar commands

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack() => _browser.GoBack();

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
        if (HasProblem)
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
    private void Home() => Navigate(_homePage);

    [RelayCommand]
    private void Print() => _browser.Print();

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

    private void Navigate(Uri uri)
    {
        Problem = null;
        _browser.Navigate(uri);
    }

    // ---------------------------------------------------------------- find in page

    public void OpenFind() => IsFindBarOpen = true;

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

            // Never overwrite what the user is typing.
            if (!_isEditingAddress)
            {
                _isAddressDirty = false;
                SetAddress(UrlDisplay.ForAddressBar(_browser.Url));
            }
            else if (!_isAddressDirty)
            {
                SetAddress(_browser.Url ?? string.Empty);
            }
        }
    }

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

    private void OnBrowserProblemOccurred(object? sender, PageProblem problem) => Problem = problem;

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
        _browser.ProblemOccurred -= OnBrowserProblemOccurred;
        _browser.PopupRequested -= OnBrowserPopupRequested;
        _browser.PopupBlocked -= OnBrowserPopupBlocked;
        _browser.OpenInNewTabRequested -= OnBrowserOpenInNewTabRequested;
        _browser.CloseRequested -= OnBrowserCloseRequested;
        _browser.Dispose();
    }
}
