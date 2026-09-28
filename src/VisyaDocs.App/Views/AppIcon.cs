using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace VisyaDocs.App.Views;

/// <summary>
/// A toolbar icon drawn from its SVG (Assets/Icons/&lt;name&gt;.svg), rasterized by Direct2D at the exact
/// device resolution so it stays sharp at any display scale. Falls back to the 4x PNG if the SVG
/// cannot be opened.
/// </summary>
public sealed partial class AppIcon : Grid
{
    private readonly Image _image = new() { Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
    private string _icon = string.Empty;
    private double _size = 20;

    public AppIcon()
    {
        Width = Height = _size;
        Children.Add(_image);
        Loaded += (_, _) => Rasterize();
    }

    /// <summary>Icon name, for example "print".</summary>
    public string Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            Load();
        }
    }

    public double Size
    {
        get => _size;
        set
        {
            _size = value;
            Width = Height = value;
            Rasterize();
        }
    }

    private void Load()
    {
        if (_icon.Length == 0) return;
        var svg = new SvgImageSource(new Uri($"ms-appx:///Assets/Icons/{_icon}.svg"));
        svg.OpenFailed += (_, _) => _image.Source = new BitmapImage(new Uri($"ms-appx:///Assets/Icons/{_icon}.scale-400.png"))
        {
            DecodePixelWidth = (int)Math.Ceiling(_size * (XamlRoot?.RasterizationScale ?? 2)),
        };
        _image.Source = svg;
        Rasterize();
    }

    private void Rasterize()
    {
        if (_image.Source is SvgImageSource svg && XamlRoot is not null)
        {
            double pixels = Math.Ceiling(_size * XamlRoot.RasterizationScale);
            svg.RasterizePixelWidth = pixels;
            svg.RasterizePixelHeight = pixels;
        }
    }
}
