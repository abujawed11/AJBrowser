using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AJBrowser.ViewModels.Pages;

namespace AJBrowser.Views.Pages;

/// <summary>ajbrowser://history. Click a row to open it here; middle-click opens it in a background tab.</summary>
public partial class HistoryPage : UserControl
{
    public HistoryPage()
    {
        InitializeComponent();
    }

    private HistoryPageViewModel? Page => DataContext as HistoryPageViewModel;

    private void Row_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Clicks on the row's delete button are handled by the button itself.
        if (e.OriginalSource is DependencyObject source && FindParent<Button>(source) is null
            && sender is FrameworkElement { DataContext: HistoryItemViewModel item })
        {
            Page?.OpenCommand.Execute(item);
        }
    }

    private void Row_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && sender is FrameworkElement { DataContext: HistoryItemViewModel item })
        {
            Page?.OpenInNewTab(item);
            e.Handled = true;
        }
    }

    internal static T? FindParent<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null and not T)
        {
            element = element is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return element as T;
    }
}
