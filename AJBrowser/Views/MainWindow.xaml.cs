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
}
