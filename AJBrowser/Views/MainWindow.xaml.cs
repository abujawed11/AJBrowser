using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;
using AJBrowser.ViewModels;

namespace AJBrowser.Views;

/// <summary>
/// The browser window. Code-behind holds only view behaviour: keyboard shortcuts, focus, fullscreen and menus.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private bool _isInFullScreen;
    private bool _isClosed;
    private bool _windowsIsShuttingDown;
    private WindowState _stateBeforeFullScreen;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;

        _viewModel.FocusAddressBarRequested += OnFocusAddressBarRequested;
        _viewModel.CloseWindowRequested += OnCloseWindowRequested;
        _viewModel.VideoPickerRequested += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            VideosPopup.IsOpen = true;
            VideosPopup.Child?.Focus();
        }, System.Windows.Threading.DispatcherPriority.Input);
        Closing += OnWindowClosing;
        Application.Current.SessionEnding += OnSessionEnding;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.Downloads.DownloadStarted += OnDownloadStarted;
        _viewModel.Downloads.DownloadCompleted += OnDownloadCompleted;
        _completedTimer.Tick += (_, _) => CloseCompletedMessage();
        Deactivated += (_, _) => CloseCompletedMessage(); // a topmost popup mustn't float over other apps
        Closed += (_, _) =>
        {
            _viewModel.Downloads.DownloadStarted -= OnDownloadStarted;
            _viewModel.Downloads.DownloadCompleted -= OnDownloadCompleted;
            _completedTimer.Stop();
            _viewModel.FocusAddressBarRequested -= OnFocusAddressBarRequested;
            _isClosed = true;
            _viewModel.CloseWindowRequested -= OnCloseWindowRequested;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            Application.Current.SessionEnding -= OnSessionEnding;
        };
    }

    // ---------------------------------------------------------------- keyboard shortcuts

    /// <summary>
    /// Browser shortcuts. PreviewKeyDown sees keys first, even when the page has focus: the WebView2 control
    /// forwards "accelerator" keys (Ctrl/Alt combinations, F-keys) to WPF, and marking them handled stops the
    /// page/browser from also acting on them (plan §10 V7).
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key; // Alt+key arrives as Key.System
        e.Handled = HandleShortcut(key, Keyboard.Modifiers);
    }

    private bool HandleShortcut(Key key, ModifierKeys modifiers)
    {
        var vm = _viewModel;
        var tab = vm.SelectedTab;

        switch (modifiers)
        {
            case ModifierKeys.Control:
                switch (key)
                {
                    case Key.T: vm.NewTabCommand.Execute(null); return true;
                    case Key.W or Key.F4: vm.CloseTabCommand.Execute(null); return true;
                    case Key.Tab or Key.PageDown: vm.NextTabCommand.Execute(null); return true;
                    case Key.PageUp: vm.PreviousTabCommand.Execute(null); return true;
                    case >= Key.D1 and <= Key.D9: vm.SelectTabByNumber(key - Key.D0); return true;
                    case >= Key.NumPad1 and <= Key.NumPad9: vm.SelectTabByNumber(key - Key.NumPad0); return true;
                    case Key.L: FocusAddressBar(); return true;
                    case Key.H: vm.ShowHistoryCommand.Execute(null); return true;
                    case Key.J: vm.ShowDownloadsCommand.Execute(null); return true;
                    case Key.D: tab?.BookmarkPageCommand.Execute(null); return true;
                    case Key.R: tab?.ReloadCommand.Execute(null); return true;
                    case Key.F: OpenFind(); return true;
                    case Key.P: tab?.PrintCommand.Execute(null); return true;
                    case Key.OemPlus or Key.Add: tab?.ZoomInCommand.Execute(null); return true;
                    case Key.OemMinus or Key.Subtract: tab?.ZoomOutCommand.Execute(null); return true;
                    case Key.D0 or Key.NumPad0: tab?.ResetZoomCommand.Execute(null); return true;
                }

                break;

            case ModifierKeys.Control | ModifierKeys.Shift:
                switch (key)
                {
                    case Key.T: vm.ReopenClosedTabCommand.Execute(null); return true;
                    case Key.O: vm.ShowBookmarksCommand.Execute(null); return true;
                    case Key.B: vm.ToggleBookmarksBarCommand.Execute(null); return true;
                    case Key.Delete: vm.ShowClearBrowsingDataCommand.Execute(null); return true;
                    case Key.Tab: vm.PreviousTabCommand.Execute(null); return true;
                    case Key.OemPlus: tab?.ZoomInCommand.Execute(null); return true; // Ctrl+Shift+= on some layouts
                }

                break;

            case ModifierKeys.Alt:
                switch (key)
                {
                    case Key.D: FocusAddressBar(); return true;

                    // Close the window ourselves: if the page had focus, WebView2 would otherwise turn Alt+F4 into a
                    // page "close window" request, which OnWindowClosing refuses (see there).
                    case Key.F4: Close(); return true;
                    case Key.Left: tab?.GoBackCommand.Execute(null); return true;
                    case Key.Right: tab?.GoForwardCommand.Execute(null); return true;
                    case Key.Home: tab?.HomeCommand.Execute(null); return true;
                }

                break;

            case ModifierKeys.Shift:
                if (key == Key.F3 && tab is { IsFindBarOpen: true })
                {
                    tab.FindPreviousCommand.Execute(null);
                    return true;
                }

                break;

            case ModifierKeys.None:
                switch (key)
                {
                    case Key.F5 or Key.BrowserRefresh: tab?.ReloadCommand.Execute(null); return true;
                    case Key.F6: FocusAddressBar(); return true;
                    case Key.F11: vm.ToggleFullScreenCommand.Execute(null); return true;
                    case Key.BrowserBack: tab?.GoBackCommand.Execute(null); return true;
                    case Key.BrowserForward: tab?.GoForwardCommand.Execute(null); return true;
                    case Key.BrowserHome: tab?.HomeCommand.Execute(null); return true;
                    case Key.F3:
                        if (tab is { IsFindBarOpen: true })
                        {
                            tab.FindNextCommand.Execute(null);
                        }
                        else
                        {
                            OpenFind();
                        }

                        return true;

                    // Esc stops loading, but text boxes (address bar, find bar) use Esc themselves.
                    case Key.Escape when tab is { IsLoading: true } && Keyboard.FocusedElement is not TextBox && !AdBlockPopup.IsOpen && !DownloadsPopup.IsOpen && !VideosPopup.IsOpen:
                        tab.Stop();
                        return true;
                }

                break;
        }

        return false;
    }

    private void FocusAddressBar()
    {
        // Wait for bindings and visibility (for example a just-created tab) before moving focus.
        Dispatcher.BeginInvoke(AddressBar.FocusAndSelectAll, DispatcherPriority.Input);
    }

    private void OpenFind()
    {
        if (_viewModel.SelectedTab is not { } tab)
        {
            return;
        }

        tab.OpenFind();
        Dispatcher.BeginInvoke(FindBar.FocusInput, DispatcherPriority.Input);
    }

    private void OnFocusAddressBarRequested(object? sender, EventArgs e) => FocusAddressBar();

    /// <summary>The last tab was closed. If closing is refused (downloads still running), show a home tab again.</summary>
    private void OnCloseWindowRequested(object? sender, EventArgs e)
    {
        Close();
        if (!_isClosed)
        {
            _viewModel.EnsureTab();
        }
    }

    /// <summary>Windows is signing out or shutting down: don't hold it up with a question.</summary>
    private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e) => _windowsIsShuttingDown = true;

    /// <summary>
    /// The WPF WebView2 control has a built-in reaction to a page calling window.close(): it closes the whole WPF
    /// window it's in (WebView2Base.CoreWebView2_WindowCloseRequested → Window.Close). That suits one-window-per-popup
    /// apps, but here every tab lives in this window, so a popup tab closing itself (sign-in popups, download links
    /// opened in a new tab) would close the entire browser (plan §11 F17). There's no option to turn it off and its
    /// handler runs before ours, so we refuse closes that come from it; BrowserTab still closes just that tab.
    /// Every other close (✕, Alt+F4, taskbar, last tab, Windows shutting down) goes through normally.
    /// </summary>
    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        // Only that specific handler. Alt+F4 inside a page would also arrive this way, so HandleShortcut closes the
        // window itself on Alt+F4 before WebView2 sees the key.
        var webViewAssembly = typeof(Microsoft.Web.WebView2.Wpf.WebView2).Assembly;
        var frames = new System.Diagnostics.StackTrace(fNeedFileInfo: false).GetFrames();
        if (frames.Any(f => f.GetMethod() is { } method
                            && method.DeclaringType?.Assembly == webViewAssembly
                            && method.Name.Contains("WindowCloseRequested", StringComparison.Ordinal)))
        {
            e.Cancel = true;
            return;
        }

        // Browser downloads die with the engine and can't continue after a restart: ask first (plan Phase 4c).
        var unfinished = _viewModel.Downloads.UnfinishedBrowserDownloads;
        if (unfinished > 0 && !_windowsIsShuttingDown)
        {
            var what = unfinished == 1 ? "1 download is" : $"{unfinished} downloads are";
            var answer = MessageBox.Show(
                this,
                $"{what} not finished. If you close AJBrowser now, {(unfinished == 1 ? "it stops" : "they stop")} and will have to start again from the beginning.\n\nClose anyway?",
                "AJBrowser",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
            }
        }
    }

    // ---------------------------------------------------------------- fullscreen

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsFullScreen))
        {
            ApplyFullScreen(_viewModel.IsFullScreen);
        }
    }

    /// <summary>Borderless and maximized covers the taskbar too. The tab strip and toolbar hide via IsChromeVisible.</summary>
    private void ApplyFullScreen(bool fullScreen)
    {
        if (fullScreen == _isInFullScreen)
        {
            return;
        }

        _isInFullScreen = fullScreen;
        if (fullScreen)
        {
            _stateBeforeFullScreen = WindowState;
            _windowChrome ??= WindowChrome.GetWindowChrome(this);
            WindowChrome.SetWindowChrome(this, null); // no drag strip or resize edges while covering the screen
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal; // re-maximize so the borderless size applies
            }

            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            if (_windowChrome is not null)
            {
                WindowChrome.SetWindowChrome(this, _windowChrome);
            }

            WindowState = _stateBeforeFullScreen;
        }

        UpdateMaximizedPadding();
    }

    // ---------------------------------------------------------------- title bar (the tab row)

    private WindowChrome? _windowChrome;

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        UpdateMaximizedPadding();
    }

    /// <summary>
    /// Without the system title bar, a maximized window extends past the screen by its (invisible) resize border,
    /// which would cut off the tab row and the edges. Pad the content by that border while maximized (not in fullscreen).
    /// </summary>
    private void UpdateMaximizedPadding()
    {
        if (Content is not FrameworkElement root)
        {
            return;
        }

        if (WindowState == WindowState.Maximized && !_isInFullScreen && WindowChrome.GetWindowChrome(this) is not null)
        {
            root.Margin = new Thickness(MaximizedOverhang());
        }
        else
        {
            root.Margin = new Thickness(0);
        }
    }

    /// <summary>How far a maximized window sticks out past the screen: the sizing frame + padded border, in WPF units.</summary>
    private double MaximizedOverhang()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return 8; // the usual value at 100% scaling
        }

        var dpi = GetDpiForWindow(hwnd);
        var pixels = GetSystemMetricsForDpi(SmCxSizeFrame, dpi) + GetSystemMetricsForDpi(SmCxPaddedBorder, dpi);
        return pixels * 96.0 / dpi;
    }

    private const int SmCxSizeFrame = 32;
    private const int SmCxPaddedBorder = 92;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    /// <summary>
    /// Windows 11 shows the snap-layout menu when the mouse rests on a window's maximize button. Windows only knows our
    /// button is one if hit-testing says so: answer WM_NCHITTEST with HTMAXBUTTON over it, then handle the
    /// non-client mouse messages ourselves (hover look, click to maximize/restore), since WPF no longer sees that mouse.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProc);
        }
    }

    private const int WmNcHitTest = 0x0084;
    private const int WmNcMouseMove = 0x00A0;
    private const int WmNcLButtonDown = 0x00A1;
    private const int WmNcLButtonUp = 0x00A2;
    private const int WmNcMouseLeave = 0x02A2;
    private const int HtMaxButton = 9;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmNcHitTest when IsOverMaximizeButton(lParam):
                handled = true;
                return HtMaxButton;

            case WmNcMouseMove:
                MaximizeButton.Tag = wParam == HtMaxButton ? (MaximizeButton.Tag as string == "Pressed" ? "Pressed" : "Hover") : null;
                break;

            case WmNcMouseLeave:
                MaximizeButton.Tag = null;
                break;

            case WmNcLButtonDown when wParam == HtMaxButton:
                MaximizeButton.Tag = "Pressed";
                handled = true; // don't let Windows run its own (invisible) maximize button logic
                break;

            case WmNcLButtonUp when wParam == HtMaxButton:
                MaximizeButton.Tag = null;
                ToggleMaximize();
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    private bool IsOverMaximizeButton(IntPtr lParam)
    {
        if (!IsChromeArea() || !MaximizeButton.IsVisible)
        {
            return false;
        }

        // lParam holds the screen position in physical pixels (signed 16-bit x and y: works on multiple monitors).
        var x = unchecked((short)(long)lParam);
        var y = unchecked((short)((long)lParam >> 16));
        try
        {
            var point = MaximizeButton.PointFromScreen(new Point(x, y)); // converts pixels to WPF units (DPI-aware)
            return point.X >= 0 && point.Y >= 0 && point.X < MaximizeButton.ActualWidth && point.Y < MaximizeButton.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false; // not connected to a window yet
        }
    }

    private bool IsChromeArea() => !_isInFullScreen && _viewModel.IsChromeVisible;

    // ---------------------------------------------------------------- menus

    /// <summary>Opens a toolbar button's ContextMenu on a normal (left) click, just below the button.</summary>
    private void OpenButtonMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void FindMenuItem_Click(object sender, RoutedEventArgs e) => OpenFind();

    // ---------------------------------------------------------------- bookmarks bar

    /// <summary>A bookmark opens in the current tab; a folder drops down a menu of its contents.</summary>
    private void BookmarkBarItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: BookmarkNodeViewModel node } button)
        {
            return;
        }

        if (!node.IsFolder)
        {
            _viewModel.OpenBookmark(node, OpenTarget.CurrentTab);
            return;
        }

        var menu = new ContextMenu
        {
            ItemsSource = node.Children.Count > 0 ? node.Children : [new BookmarkNodeViewModel(new Core.Persistence.Bookmark(0, null, "(empty)", null, 0, default), [])],
            ItemContainerStyle = (Style)FindResource("BookmarkMenuItemStyle"),
            FontFamily = (System.Windows.Media.FontFamily)FindResource("UiFontFamily"),
            FontSize = (double)FindResource("BodyFontSize"),
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
        };
        menu.IsOpen = true;
    }

    /// <summary>Middle-click on a bookmark: open it in a background tab.</summary>
    private void BookmarkBarItem_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && sender is FrameworkElement { DataContext: BookmarkNodeViewModel { IsFolder: false } node })
        {
            _viewModel.OpenBookmark(node, OpenTarget.NewBackgroundTab);
            e.Handled = true;
        }
    }

    /// <summary>A bookmark inside a bookmarks-bar folder menu (folders open submenus and don't raise Click).</summary>
    private void BookmarkMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: BookmarkNodeViewModel node } && ReferenceEquals(e.OriginalSource, sender))
        {
            _viewModel.OpenBookmark(node, OpenTarget.CurrentTab);
        }
    }

    // ---------------------------------------------------------------- ad blocker panel

    private long _adBlockPopupClosedAt;

    /// <summary>The shield toggles its panel. Clicking the shield while the panel is open first closes it
    /// (StaysOpen=False closes on any outside click), so don't immediately reopen it.</summary>
    private void AdBlockButton_Click(object sender, RoutedEventArgs e)
    {
        if (Environment.TickCount64 - _adBlockPopupClosedAt < 250)
        {
            return;
        }

        AdBlockPopup.IsOpen = true;
        AdBlockSwitch.Focus(); // so Space toggles and Esc closes straight away
    }

    private void AdBlockPopup_Closed(object? sender, EventArgs e) => _adBlockPopupClosedAt = Environment.TickCount64;

    private void AdBlockPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            AdBlockPopup.IsOpen = false;
            AdBlockButton.Focus();
            e.Handled = true;
        }
    }

    /// <summary>The Settings link runs its command (opens a tab); also close the panel.</summary>
    private void AdBlockSettings_Click(object sender, RoutedEventArgs e) => AdBlockPopup.IsOpen = false;

    // ---------------------------------------------------------------- videos popup (Phase 5)

    private long _videosPopupClosedAt;

    private void VideosButton_Click(object sender, RoutedEventArgs e)
    {
        if (Environment.TickCount64 - _videosPopupClosedAt < 250)
        {
            return;
        }

        _viewModel.SelectedTab?.OnVideoPickerOpened();
        VideosPopup.IsOpen = true;
    }

    private void VideosPopup_Closed(object? sender, EventArgs e) => _videosPopupClosedAt = Environment.TickCount64;

    private void VideosPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            VideosPopup.IsOpen = false;
            VideosButton.Focus();
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- downloads flyout

    private long _downloadsPopupClosedAt;

    /// <summary>Toggles the flyout (same double-click guard as the shield's panel).</summary>
    private void DownloadsButton_Click(object sender, RoutedEventArgs e)
    {
        if (Environment.TickCount64 - _downloadsPopupClosedAt < 250)
        {
            return;
        }

        DownloadsPopup.IsOpen = true;
    }

    /// <summary>A page started a download: show it in the flyout, like Chrome/Edge (not over a fullscreen video).</summary>
    private void OnDownloadStarted(object? sender, EventArgs e)
    {
        if (!_viewModel.IsFullScreen && IsActive)
        {
            DownloadsPopup.IsOpen = true;
        }
    }

    private void DownloadsPopup_Opened(object? sender, EventArgs e)
    {
        CloseCompletedMessage();
        _viewModel.Downloads.MarkCompletedSeen();
        _viewModel.Downloads.RefreshFiles();
    }

    // ---------------------------------------------------------------- "download complete"

    private readonly DispatcherTimer _completedTimer = new() { Interval = TimeSpan.FromSeconds(7) };
    private DownloadItemViewModel? _completedItem;

    /// <summary>
    /// A download finished. In front: a short message under the downloads button (not over a fullscreen video, not while
    /// the downloads list is open, which shows it anyway). In the background or minimized: the taskbar button flashes
    /// until AJBrowser is brought back. The green dot on the button stays until the list is opened.
    /// </summary>
    private void OnDownloadCompleted(object? sender, DownloadItemViewModel item)
    {
        if (!IsActive || WindowState == WindowState.Minimized)
        {
            FlashTaskbar();
            return;
        }

        if (_viewModel.IsFullScreen || DownloadsPopup.IsOpen)
        {
            return;
        }

        _completedItem = item;
        CompletedFileName.Text = item.FileName;
        CompletedFileName.ToolTip = item.Item.FilePath;
        CompletedPopup.IsOpen = true;
        _completedTimer.Stop();
        _completedTimer.Start();
    }

    private void CloseCompletedMessage()
    {
        _completedTimer.Stop();
        CompletedPopup.IsOpen = false;
    }

    private void CompletedOpen_Click(object sender, RoutedEventArgs e)
    {
        _completedItem?.OpenCommand.Execute(null);
        _viewModel.Downloads.MarkCompletedSeen();
        CloseCompletedMessage();
    }

    private void CompletedShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        _completedItem?.ShowInFolderCommand.Execute(null);
        _viewModel.Downloads.MarkCompletedSeen();
        CloseCompletedMessage();
    }

    private void CompletedClose_Click(object sender, RoutedEventArgs e) => CloseCompletedMessage();

    /// <summary>Flashes the taskbar button until the window comes to the front (Windows' usual "something happened").</summary>
    private void FlashTaskbar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var info = new FlashWindowInfo
        {
            Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
            Hwnd = hwnd,
            Flags = FlashTray | FlashUntilForeground,
            Count = uint.MaxValue,
            Timeout = 0,
        };
        FlashWindowEx(ref info);
    }

    private const uint FlashTray = 0x2;
    private const uint FlashUntilForeground = 0xC;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public IntPtr Hwnd;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashWindowInfo info);

    private void DownloadsPopup_Closed(object? sender, EventArgs e) => _downloadsPopupClosedAt = Environment.TickCount64;

    private void DownloadPanel_CloseRequested(object? sender, EventArgs e) => DownloadsPopup.IsOpen = false;

    private void DownloadsPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DownloadsPopup.IsOpen = false;
            DownloadsButton.Focus();
            e.Handled = true;
        }
    }

    private void OpenDiagnostics_Click(object sender, RoutedEventArgs e)
    {
#if DEBUG
        ((App)Application.Current).OpenDiagnostics();
#endif
    }
}
