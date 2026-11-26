using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
    private WindowState _stateBeforeFullScreen;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;

        _viewModel.FocusAddressBarRequested += OnFocusAddressBarRequested;
        _viewModel.CloseWindowRequested += OnCloseWindowRequested;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += (_, _) =>
        {
            _viewModel.FocusAddressBarRequested -= OnFocusAddressBarRequested;
            _viewModel.CloseWindowRequested -= OnCloseWindowRequested;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
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
                    case Key.Escape when tab is { IsLoading: true } && Keyboard.FocusedElement is not TextBox && !AdBlockPopup.IsOpen:
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

    private void OnCloseWindowRequested(object? sender, EventArgs e) => Close();

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
            WindowState = _stateBeforeFullScreen;
        }
    }

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

    private void OpenDiagnostics_Click(object sender, RoutedEventArgs e)
    {
#if DEBUG
        ((App)Application.Current).OpenDiagnostics();
#endif
    }
}
