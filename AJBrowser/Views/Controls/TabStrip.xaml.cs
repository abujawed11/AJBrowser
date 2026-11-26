using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AJBrowser.ViewModels;

namespace AJBrowser.Views.Controls;

/// <summary>The row of tabs above the toolbar.</summary>
public partial class TabStrip : UserControl
{
    public TabStrip()
    {
        InitializeComponent();
    }

    /// <summary>Middle-click closes a tab, like in other browsers.</summary>
    private void Tab_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle
            && sender is FrameworkElement { DataContext: TabViewModel tab }
            && DataContext is MainWindowViewModel window)
        {
            window.CloseTabCommand.Execute(tab);
            e.Handled = true;
        }
    }
}
