using System.Windows;
using System.Windows.Controls.Primitives;
using AJBrowser.ViewModels;

namespace AJBrowser.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        // Open the button's ContextMenu on a normal (left) click, just below the button.
        var menu = MenuButton.ContextMenu;
        menu.PlacementTarget = MenuButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OpenDiagnostics_Click(object sender, RoutedEventArgs e)
    {
#if DEBUG
        ((App)Application.Current).OpenDiagnostics();
#endif
    }
}
