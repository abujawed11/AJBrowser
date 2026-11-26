using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AJBrowser.Views.Controls;

/// <summary>
/// A 16 px site icon: shows <see cref="Source"/> (the favicon) when there is one, otherwise the Segoe Fluent Icons
/// <see cref="Glyph"/> (a globe, a folder, a star…). Used by History, Bookmarks, the bookmarks bar and suggestions.
/// Like a tiny React component: <c>&lt;SiteIcon Source={icon} Glyph="🌐" /&gt;</c>.
/// </summary>
public sealed class SiteIcon : Grid
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ImageSource), typeof(SiteIcon), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(SiteIcon), new PropertyMetadata("", OnChanged));

    private readonly Image _image = new() { Width = 16, Height = 16 };
    private readonly TextBlock _glyph = new()
    {
        FontSize = 14,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public SiteIcon()
    {
        Width = 16;
        Height = 16;
        VerticalAlignment = VerticalAlignment.Center;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFontFamily");
        _glyph.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        Children.Add(_image);
        Children.Add(_glyph);
        Update();
    }

    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((SiteIcon)d).Update();

    private void Update()
    {
        _image.Source = Source;
        _image.Visibility = Source is null ? Visibility.Collapsed : Visibility.Visible;
        _glyph.Text = Glyph;
        _glyph.Visibility = Source is null ? Visibility.Visible : Visibility.Collapsed;
    }
}
