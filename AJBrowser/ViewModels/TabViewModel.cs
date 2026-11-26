using AJBrowser.Browser;
using AJBrowser.Core.Logging;
using AJBrowser.Core.Navigation;
using AJBrowser.Core.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AJBrowser.ViewModels;

/// <summary>
/// UI state for one tab: address bar text, title, loading, back/forward, and the problem panel.
/// Think of it as the React state + handlers for a tab; <see cref="BrowserTab"/> is the "API" it calls.
/// </summary>
public sealed partial class TabViewModel : ObservableObject, IDisposable
{
    private const string AppName = "AJBrowser";

    private readonly BrowserTab _browser;
    private readonly UrlOrSearchResolver _resolver;
    private readonly Uri _homePage;
    private readonly AppLogger _log;

    private string? _lastUrl;
    private bool _isEditingAddress;
    private bool _isAddressDirty;   // the user typed something that hasn't been navigated to or reverted
    private bool _isSettingAddress; // we are changing AddressText ourselves, not the user

    public TabViewModel(BrowserTab browser, UrlOrSearchResolver resolver, Uri homePage, AppLogger log)
    {
        _browser = browser;
        _resolver = resolver;
        _homePage = homePage;
        _log = log;

        _browser.StateChanged += OnBrowserStateChanged;
        _browser.NavigationStarted += OnBrowserNavigationStarted;
        _browser.ProblemOccurred += OnBrowserProblemOccurred;
    }

    /// <summary>The page element to show in the window (hosts the WebView2 control).</summary>
    public object BrowserView => _browser.View;

    /// <summary>Text in the address bar: the clean URL normally, the full URL while editing, or what the user typed.</summary>
    [ObservableProperty]
    public partial string AddressText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    public partial string Title { get; set; } = "New tab";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoBackCommand))]
    public partial bool CanGoBack { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoForwardCommand))]
    public partial bool CanGoForward { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>When set, the page is hidden and a message panel is shown instead.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem), nameof(IsBrowserVisible))]
    public partial PageProblem? Problem { get; set; }

    public bool HasProblem => Problem is not null;

    /// <summary>
    /// The WebView is hidden while a problem panel is shown: WPF can't draw on top of a WebView2
    /// ("airspace", plan §3), so the panel and the page take turns.
    /// </summary>
    public bool IsBrowserVisible => Problem is null;

    public string WindowTitle => string.IsNullOrWhiteSpace(Title) ? AppName : $"{Title} - {AppName}";

    /// <summary>Starts the web engine and loads the home page.</summary>
    public async Task StartAsync()
    {
        _browser.Navigate(_homePage); // queued until the engine is ready
        await _browser.InitializeAsync();
    }

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
        if (HasProblem)
        {
            await Retry();
        }
        else if (IsLoading)
        {
            _browser.Stop();
        }
        else
        {
            _browser.Reload();
        }
    }

    [RelayCommand]
    private void Home() => Navigate(_homePage);

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

    private void Navigate(Uri uri)
    {
        Problem = null;
        _browser.Navigate(uri);
    }

    // ---------------------------------------------------------------- browser events

    private void OnBrowserStateChanged(object? sender, EventArgs e)
    {
        Title = !string.IsNullOrWhiteSpace(_browser.Title) ? _browser.Title : UrlDisplay.ForAddressBar(_browser.Url);
        CanGoBack = _browser.CanGoBack;
        CanGoForward = _browser.CanGoForward;
        IsLoading = _browser.IsLoading;

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
        // A new page is loading, so an old error/crash message no longer applies.
        Problem = null;
    }

    private void OnBrowserProblemOccurred(object? sender, PageProblem problem) => Problem = problem;

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
        _browser.Dispose();
    }
}
