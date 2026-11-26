using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AJBrowser.ViewModels.Pages;

namespace AJBrowser.Views.Pages;

/// <summary>ajbrowser://bookmarks. Click selects, double-click (or Enter) opens, middle-click opens in a background tab.</summary>
public partial class BookmarksPage : UserControl
{
    public BookmarksPage()
    {
        InitializeComponent();
    }

    private BookmarksPageViewModel? Page => DataContext as BookmarksPageViewModel;

    private void Row_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: BookmarkItemViewModel item })
        {
            Page?.OpenItemCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void Row_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && sender is FrameworkElement { DataContext: BookmarkItemViewModel item })
        {
            Page?.OpenInNewTab(item);
            e.Handled = true;
        }
    }

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (Page is not { } page)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter when page.SelectedItem is { } item:
                page.OpenItemCommand.Execute(item);
                e.Handled = true;
                break;

            case Key.Back when page.GoUpCommand.CanExecute(null):
                page.GoUpCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Delete when page.DeleteSelectedCommand.CanExecute(null):
                page.DeleteSelectedCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
}
