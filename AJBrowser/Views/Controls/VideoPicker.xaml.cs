using System.Windows.Controls;

namespace AJBrowser.Views.Controls;

/// <summary>The "Videos on this page" list in the toolbar's video popup. Behaviour lives in TabViewModel / DetectedMediaViewModel.</summary>
public partial class VideoPicker : UserControl
{
    public VideoPicker()
    {
        InitializeComponent();
    }
}
