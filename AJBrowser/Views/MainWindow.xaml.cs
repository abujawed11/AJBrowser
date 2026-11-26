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
                    case Key.Escape when tab is { IsLoading: true } && Keyboard.FocusedElement is not TextBox:
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

    private void OpenDiagnostics_Click(object sender, RoutedEventArgs e)
    {
#if DEBUG
        ((App)Application.Current).OpenDiagnostics();
#endif
    }
}
