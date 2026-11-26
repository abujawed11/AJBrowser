using System.Windows;
using AJBrowser.ViewModels;

namespace AJBrowser.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OpenDiagnostics_Click(object sender, RoutedEventArgs e)
    {
#if DEBUG
        ((App)Application.Current).OpenDiagnostics();
#endif
    }
}
