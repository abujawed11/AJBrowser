using System.Windows.Controls;
using System.Windows.Input;
using AJBrowser.ViewModels;

namespace AJBrowser.Views.Controls;

/// <summary>Find-in-page bar: Enter = next match, Shift+Enter = previous, Esc = close.</summary>
public partial class FindBar : UserControl
{
    public FindBar()
    {
        InitializeComponent();
    }

    public void FocusInput()
    {
        FindBox.Focus();
        FindBox.SelectAll();
    }

    private void FindBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not TabViewModel tab)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.Shift:
                tab.FindPreviousCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Enter:
                tab.FindNextCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Escape:
                tab.CloseFindCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
}
