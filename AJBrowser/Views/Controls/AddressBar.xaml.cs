using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AJBrowser.ViewModels;

namespace AJBrowser.Views.Controls;

/// <summary>
/// The address bar. Behaviour (like browsers): selects everything when it gets focus, shows the full URL
/// while editing, Enter navigates or searches, Esc throws the edit away.
/// </summary>
public partial class AddressBar : UserControl
{
    public AddressBar()
    {
        InitializeComponent();
    }

    private TabViewModel? Tab => DataContext as TabViewModel;

    private void AddressBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // The first click should select everything, not place the caret. Later clicks behave normally.
        if (!AddressBox.IsKeyboardFocusWithin)
        {
            AddressBox.Focus();
            e.Handled = true;
        }
    }

    private void AddressBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        Tab?.BeginAddressEdit();
        // Select after the text swap (clean URL -> full URL) has reached the TextBox.
        Dispatcher.BeginInvoke(AddressBox.SelectAll, DispatcherPriority.Input);
    }

    private void AddressBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => Tab?.EndAddressEdit();

    private void AddressBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Tab is not { } tab)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                tab.NavigateToAddressCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Escape:
                tab.RevertAddressCommand.Execute(null);
                AddressBox.SelectAll();
                e.Handled = true;
                break;
        }
    }
}
