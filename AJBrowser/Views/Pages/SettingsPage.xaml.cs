using System.Windows.Controls;
using System.Windows.Input;
using AJBrowser.ViewModels.Pages;

namespace AJBrowser.Views.Pages;

/// <summary>ajbrowser://settings. Text boxes apply on Enter or when you leave them; the folder dialog lives here (it's UI).</summary>
public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    private SettingsPageViewModel? Page => DataContext as SettingsPageViewModel;

    private void HomePage_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => Page?.ApplyHomePageCommand.Execute(null);

    private void HomePage_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Page?.ApplyHomePageCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void CustomSearch_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => Page?.ApplyCustomSearchCommand.Execute(null);

    private void CustomSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Page?.ApplyCustomSearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Pick ffmpeg.exe (AJBrowser only runs it on local files it created; it never downloads it).</summary>
    /// <summary>Choose… on a tool row: your own copy of yt-dlp, Deno or FFmpeg.</summary>
    private void ChooseTool_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if ((sender as System.Windows.FrameworkElement)?.Tag is not ViewModels.Pages.ToolRowViewModel row)
        {
            return;
        }

        var exe = Core.Tools.ToolCatalog.For(row.Kind).ExeName;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Choose {exe}",
            Filter = $"{row.Name} ({exe})|{exe}|Programs (*.exe)|*.exe",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(System.Windows.Window.GetWindow(this)) == true)
        {
            row.ChoosePath(dialog.FileName);
        }
    }

    private void ChangeDownloadFolder_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose where downloads are saved" };
        if (dialog.ShowDialog(System.Windows.Window.GetWindow(this)) == true)
        {
            Page?.SetDownloadFolder(dialog.FolderName);
        }
    }
}
