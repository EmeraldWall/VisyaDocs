using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace VisaryPDF.App.Views;

/// <summary>
/// A toolbar icon drawn from its SVG, rasterized by Direct2D at the exact device resolution so it
/// stays sharp at any display scale. The light theme uses the darker set in Assets/Icons/Light,
/// dark and black themes use Assets/Icons, so the icon keeps its contrast against the background.
/// </summary>
public sealed partial class AppIcon : Grid
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(string), typeof(AppIcon), new PropertyMetadata(string.Empty, (d, _) => ((AppIcon)d).Load()));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(AppIcon), new PropertyMetadata(20.0, (d, _) => ((AppIcon)d).Resize()));

    private readonly Image _image = new() { Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
    private string _loaded = string.Empty;

    public AppIcon()
    {
        Width = Height = Size;
        Children.Add(_image);
        Loaded += (_, _) => Load();
        ActualThemeChanged += (_, _) => Load();
    }

    /// <summary>Icon name, for example "print".</summary>
    public string Icon
    {
        get => (string)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    private void Resize()
    {
        Width = Height = Size;
        Rasterize();
    }

    private void Load()
    {
        if (string.IsNullOrEmpty(Icon)) return;
        string folder = ActualTheme == ElementTheme.Light ? "Light/" : "";
        string uri = $"ms-appx:///Assets/Icons/{folder}{Icon}.svg";
        if (uri != _loaded)
        {
            _loaded = uri;
            _image.Source = new SvgImageSource(new Uri(uri));
        }
        Rasterize();
    }

    private void Rasterize()
    {
        if (_image.Source is SvgImageSource svg && XamlRoot is not null)
        {
            double pixels = Math.Ceiling(Size * XamlRoot.RasterizationScale);
            svg.RasterizePixelWidth = pixels;
            svg.RasterizePixelHeight = pixels;
        }
    }
}
