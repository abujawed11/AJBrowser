using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AJBrowser.ViewModels;
using AJBrowser.Views.Pages;

namespace AJBrowser.Views.Controls;

/// <summary>The downloads flyout's content. Asks its host to close after actions that move elsewhere.</summary>
public partial class DownloadPanel : UserControl
{
    public DownloadPanel()
    {
        InitializeComponent();
    }

    /// <summary>The user chose something that leaves the flyout (opened a file, a folder, the Downloads page…).</summary>
    public event EventHandler? CloseRequested;

    private void ClosingButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Clicking a finished download opens it (clicks on the row's own buttons are left to them).</summary>
    private void Row_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && HistoryPage.FindParent<Button>(source) is null
            && sender is FrameworkElement { DataContext: DownloadItemViewModel { CanOpen: true } item })
        {
            item.OpenCommand.Execute(null);
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
