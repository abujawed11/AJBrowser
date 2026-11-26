using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AJBrowser.ViewModels;

namespace AJBrowser.Views.Controls;

/// <summary>
/// The address bar. Behaviour (like browsers): selects everything when it gets focus, shows the full URL
/// while editing, suggests pages from history and bookmarks while typing (↑/↓ to pick), Enter navigates or
/// searches, Esc closes the suggestions and then throws the edit away. Also hosts the bookmark star and its panel.
/// </summary>
public partial class AddressBar : UserControl
{
    public AddressBar()
    {
        InitializeComponent();

        // Anchors set in code: an ElementName binding on these popups didn't resolve (the panel opened at the far left).
        SuggestionsPopup.PlacementTarget = AddressBox;
        BookmarkPopup.PlacementTarget = StarButton;
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// The selected tab changed while the box has focus (Ctrl+T focuses the box while the new tab is being selected):
    /// the old tab stops editing and the new one starts, otherwise the new tab wouldn't know the user is typing.
    /// </summary>
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!AddressBox.IsKeyboardFocusWithin)
        {
            return;
        }

        (e.OldValue as TabViewModel)?.EndAddressEdit();
        if (e.NewValue is TabViewModel tab)
        {
            tab.BeginAddressEdit();
            Dispatcher.BeginInvoke(AddressBox.SelectAll, DispatcherPriority.Input);
        }
    }

    private TabViewModel? Tab => DataContext as TabViewModel;

    /// <summary>Ctrl+L / Alt+D / F6 and new tabs: put the caret here with everything selected.</summary>
    public void FocusAndSelectAll()
    {
        if (AddressBox.IsKeyboardFocusWithin)
        {
            AddressBox.SelectAll();
        }
        else
        {
            AddressBox.Focus(); // GotKeyboardFocus selects everything
        }
    }

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

            case Key.Down:
                e.Handled = tab.Suggestions.Move(+1);
                break;

            case Key.Up:
                e.Handled = tab.Suggestions.Move(-1);
                break;

            case Key.Escape:
                if (tab.Suggestions.IsOpen)
                {
                    tab.Suggestions.Close(); // first Esc: just close the list
                }
                else
                {
                    tab.RevertAddressCommand.Execute(null);
                    AddressBox.SelectAll();
                }

                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Mouse down (not click) on a suggestion: acts before the text box could lose focus and close the list.
    /// Middle button isn't handled here; a left press navigates.
    /// </summary>
    private void Suggestion_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: SuggestionItem suggestion } && Tab is { } tab)
        {
            tab.NavigateToSuggestion(suggestion);
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- bookmark panel

    private void BookmarkPopup_Opened(object? sender, EventArgs e)
    {
        // Focus the name, selected, so it can be retyped straight away (like Chrome).
        Dispatcher.BeginInvoke(() =>
        {
            BookmarkNameBox.Focus();
            BookmarkNameBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    /// <summary>Closed by Done, Remove, Esc or a click elsewhere: save what changed.</summary>
    private async void BookmarkPopup_Closed(object? sender, EventArgs e)
    {
        // async void: an event handler; CloseBookmarkEditorAsync logs its own failures.
        if (Tab is { } tab)
        {
            await tab.CloseBookmarkEditorAsync();
        }
    }

    private void BookmarkPopupClose_Click(object sender, RoutedEventArgs e) => BookmarkPopup.IsOpen = false;

    private void BookmarkPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.Enter && e.OriginalSource is not ComboBox)
        {
            BookmarkPopup.IsOpen = false;
            e.Handled = true;
        }
    }
}
